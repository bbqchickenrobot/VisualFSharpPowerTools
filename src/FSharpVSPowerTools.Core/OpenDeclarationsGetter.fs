﻿namespace FSharpVSPowerTools

open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.Ast
open Microsoft.FSharp.Compiler.SourceCodeServices

type RawOpenDeclaration =
    { Idents: Idents
      Parent: Idents }

type OpenDeclWithAutoOpens =
    { Declarations: Idents list
      Parent: Idents 
      IsUsed: bool }

[<NoComparison>]
type OpenDeclaration =
    { Declarations: OpenDeclWithAutoOpens list
      DeclarationRange: Range.range
      ScopeRange: Range.range
      IsUsed: bool }

[<CompilationRepresentation (CompilationRepresentationFlags.ModuleSuffix)>]
module OpenDeclWithAutoOpens =
    let updateBySymbolPrefix (symbolPrefix: Idents) (decl: OpenDeclWithAutoOpens) =
        let matched = decl.Declarations |> List.exists ((=) symbolPrefix)
        if not decl.IsUsed && matched then debug "OpenDeclarationWithAutoOpens %A is used by %A" decl symbolPrefix
        matched, { decl with IsUsed = decl.IsUsed || matched }

[<CompilationRepresentation (CompilationRepresentationFlags.ModuleSuffix)>]
module OpenDeclaration =
    let updateBySymbolPrefix (symbolPrefix: Idents) (decl: OpenDeclaration) =
        let decls =
            decl.Declarations 
            |> List.map (OpenDeclWithAutoOpens.updateBySymbolPrefix symbolPrefix)
        let matched = decls |> List.exists fst
        let isUsed = decls |> List.exists (fun (_, decl) -> decl.IsUsed)
        if not decl.IsUsed && isUsed then debug "OpenDeclaration %A is used by %A" decl symbolPrefix
        matched, { decl with Declarations = decls |> List.map snd; IsUsed = isUsed }

module OpenDeclarationGetter =
    open UntypedAstUtils
    open System

    let getAutoOpenModules entities =
        entities 
        |> List.filter (fun e -> 
             match e.Kind with
             | EntityKind.Module { IsAutoOpen = true } -> true
             | _ -> false)
        |> List.map (fun e -> e.Idents)

    let getModulesWithModuleSuffix entities =
        entities 
        |> List.choose (fun e -> 
            match e.Kind with
            | EntityKind.Module { HasModuleSuffix = true } ->
                // remove Module suffix
                let lastIdent = e.Idents.[e.Idents.Length - 1]
                let result = Array.copy e.Idents
                result.[result.Length - 1] <- lastIdent.Substring (0, lastIdent.Length - 6)
                Some result
            | _ -> None)

    let parseTooltip (ToolTipText elems): RawOpenDeclaration list =
        elems
        |> List.map (fun e -> 
            let rawStrings =
                match e with
                | ToolTipElement.ToolTipElement (s, _) -> [s]
                | ToolTipElement.ToolTipElementGroup elems -> 
                    elems |> List.map (fun (s, _) -> s)
                | _ -> []
            
            let removePrefix prefix (str: string) =
                if str.StartsWith prefix then Some (str.Substring(prefix.Length).Trim()) else None

            rawStrings
            |> List.choose (fun (s: string) ->
                 maybe {
                    let! name, from = 
                        match s.Split ([|'\n'|], StringSplitOptions.RemoveEmptyEntries) with
                        | [|name; from|] -> Some (name, from)
                        | [|name|] -> Some (name, "")
                        | _ -> None

                    let! name = 
                        name 
                        |> removePrefix "namespace"
                        |> Option.orElse (name |> removePrefix "module")
                     
                    let from = from |> removePrefix "from" |> Option.map (fun s -> s + ".") |> Option.getOrElse ""
                    return { RawOpenDeclaration.Idents = (from + name).Split '.'; Parent = from.Split '.' }
                }))
        |> List.concat

    let updateOpenDeclsWithSymbolPrefix symbolPrefix symbolRange openDecls = 
        openDecls 
        |> List.fold (fun (acc, foundMatchedDecl) openDecl -> 
            // We already found a matched open declaration or the symbol range is out or the next open declaration.
            // Do nothing, just add the open decl to the accumulator as is.
            if foundMatchedDecl || not (Range.rangeContainsRange openDecl.ScopeRange symbolRange) then
                openDecl :: acc, foundMatchedDecl
            else
                let matched, updatedDecl = openDecl |> OpenDeclaration.updateBySymbolPrefix symbolPrefix
                updatedDecl :: acc, matched
            ) ([], false)
        |> fst
        |> List.rev

    type Line = int
    type EndColumn = int

    let getOpenDeclarations (ast: ParsedInput option) (entities: RawEntity list option) 
                            (qualifyOpenDeclarations: Line -> EndColumn -> Idents -> RawOpenDeclaration list) =
        match ast, entities with
        | Some (ParsedInput.ImplFile (ParsedImplFileInput(_, _, _, _, _, modules, _))), Some entities ->
            let autoOpenModules = getAutoOpenModules entities
            debug "All AutoOpen modules: %A" autoOpenModules
            let modulesWithModuleSuffix = getModulesWithModuleSuffix entities

            let rec walkModuleOrNamespace acc (decls, moduleRange) =
                let openStatements =
                    decls
                    |> List.fold (fun acc -> 
                        function
                        | SynModuleDecl.NestedModule (_, nestedModuleDecls, _, nestedModuleRange) -> 
                            walkModuleOrNamespace acc (nestedModuleDecls, nestedModuleRange)
                        | SynModuleDecl.Open (LongIdentWithDots(ident, _), openStatementRange) ->
                            let rawOpenDeclarations = 
                                longIdentToArray ident
                                |> qualifyOpenDeclarations openStatementRange.StartLine openStatementRange.EndColumn

                            (* The idea that each open declaration can actually open itself and all direct AutoOpen modules,
                                children AutoOpen modules and so on until a non-AutoOpen module is met.
                                Example:
                                   
                                module M =
                                    [<AutoOpen>]                                  
                                    module A1 =
                                        [<AutoOpen>] 
                                        module A2 =
                                            module A3 = 
                                                [<AutoOpen>] 
                                                module A4 = ...
                                         
                                // this declaration actually open M, M.A1, M.A1.A2, but NOT M.A1.A2.A3 or M.A1.A2.A3.A4
                                open M
                            *)

                            let rec loop acc maxLength =
                                let newModules =
                                    autoOpenModules
                                    |> List.filter (fun autoOpenModule -> 
                                        autoOpenModule.Length = maxLength + 1
                                        && acc |> List.exists (fun collectedAutoOpenModule ->
                                            autoOpenModule |> Array.startsWith collectedAutoOpenModule))
                                match newModules with
                                | [] -> acc
                                | _ -> loop (acc @ newModules) (maxLength + 1)
                                
                            let identsAndAutoOpens = 
                                rawOpenDeclarations
                                |> List.map (fun openDecl -> 
                                     { Declarations = loop [openDecl.Idents] openDecl.Idents.Length 
                                       Parent = openDecl.Parent
                                       IsUsed = false })

                            (* For each module that has ModuleSuffix attribute value we add additional Idents "<Name>Module". For example:
                                   
                                module M =
                                    [<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
                                    module M1 =
                                        module M2 =
                                            let func _ = ()
                                open M.M1.M2
                                The last line will produce two Idents: "M.M1.M2" and "M.M1Module.M2".
                                The reason is that FCS return different FullName for entities declared in ModuleSuffix modules
                                depending on thether the module is in the current project or not. 
                            *)
                            let finalOpenDecls = 
                                identsAndAutoOpens
                                |> List.map (fun openDecl ->
                                    let idents =
                                        openDecl.Declarations
                                        |> List.map (fun idents ->
                                            [ yield idents 
                                              match modulesWithModuleSuffix |> List.tryFind (fun m -> idents |> Array.startsWith m) with
                                              | Some m ->
                                                  let index = (Array.length m) - 1
                                                  let modifiedIdents = Array.copy idents
                                                  modifiedIdents.[index] <- idents.[index] + "Module"
                                                  yield modifiedIdents
                                              | None -> ()])
                                        |> List.concat
                                    { openDecl with Declarations = idents })
                                |> Seq.distinct
                                |> Seq.toList

                            { Declarations = finalOpenDecls
                              DeclarationRange = openStatementRange
                              ScopeRange = Range.mkRange openStatementRange.FileName openStatementRange.Start moduleRange.End
                              IsUsed = false } :: acc
                        | _ -> acc) [] 
                openStatements @ acc

            modules
            |> List.fold (fun acc (SynModuleOrNamespace(_, _, decls, _, _, _, moduleRange)) ->
                 walkModuleOrNamespace acc (decls, moduleRange) @ acc) []       
        | _ -> [] 

