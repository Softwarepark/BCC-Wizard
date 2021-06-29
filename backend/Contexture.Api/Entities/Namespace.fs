module Contexture.Api.Aggregates.Namespace

open System
open Contexture.Api.Entities

type Errors =
    | EmptyName
    | NamespaceNameNotUnique

type Command =
    | NewNamespace of BoundedContextId * NamespaceDefinition
    | RemoveNamespace of BoundedContextId * NamespaceId
    | RemoveLabel of BoundedContextId * RemoveLabel
    | AddLabel of BoundedContextId * NamespaceId * NewLabelDefinition

and NamespaceDefinition =
    { Name: string
      Template: NamespaceTemplateId option
      Labels: NewLabelDefinition list }

and NewLabelDefinition = { Name: string; Value: string; Template: TemplateLabelId option }

and RemoveLabel =
    { Namespace: NamespaceId
      Label: LabelId }

type Event =
    | NamespaceImported of NamespaceImported
    | NamespaceAdded of NamespaceAdded
    | NamespaceRemoved of NamespaceRemoved
    | LabelRemoved of LabelRemoved
    | LabelAdded of LabelAdded

and NamespaceImported =
    { NamespaceId: NamespaceId
      BoundedContextId: BoundedContextId
      NamespaceTemplateId: NamespaceTemplateId option
      Name: string
      Labels: LabelDefinition list }

and NamespaceAdded =
    { NamespaceId: NamespaceId
      BoundedContextId: BoundedContextId
      NamespaceTemplateId: NamespaceTemplateId option
      Name: string
      Labels: LabelDefinition list }

and NamespaceRemoved = { NamespaceId: NamespaceId }

and LabelDefinition =
    { LabelId: LabelId
      Name: string
      Value: string option
      Template: TemplateLabelId option }

and LabelRemoved =
    { NamespaceId: NamespaceId
      LabelId: LabelId }

and LabelAdded =
    { LabelId: LabelId
      NamespaceId: NamespaceId
      Name: string
      Value: string option }

type State =
    | Namespaces of Map<NamespaceId, string>
    static member Initial = Namespaces Map.empty

    static member Fold (Namespaces namespaces) (event: Event) =
        match event with
        | NamespaceRemoved e ->
            namespaces
            |> Map.remove e.NamespaceId
            |> Namespaces
        | NamespaceAdded e ->
            namespaces
            |> Map.add e.NamespaceId e.Name
            |> Namespaces
        | NamespaceImported e ->
            namespaces
            |> Map.add e.NamespaceId e.Name
            |> Namespaces
        | _ -> Namespaces namespaces

module LabelDefinition =
    let create name (value: string) template: LabelDefinition option =
        if String.IsNullOrWhiteSpace name then
            None
        else
            Some
                { LabelId = Guid.NewGuid()
                  Name = name.Trim()
                  Value = if not (isNull value) then value.Trim() |> Some else None
                  Template = template }

let addNewNamespace boundedContextId name templateId (labels: NewLabelDefinition list) (Namespaces namespaces) =
    if namespaces
       |> Map.exists (fun _ existingName -> String.Equals (existingName, name,StringComparison.OrdinalIgnoreCase)) then
        Error NamespaceNameNotUnique
    else
        let newLabels =
            labels
            |> List.choose (fun label -> LabelDefinition.create label.Name label.Value label.Template)

        let newNamespace =
            NamespaceAdded
                { NamespaceId = Guid.NewGuid()
                  BoundedContextId = boundedContextId
                  NamespaceTemplateId = templateId
                  Name = name
                  Labels = newLabels }

        Ok newNamespace

let addLabel namespaceId labelName value =
    match LabelDefinition.create labelName value None with
    | Some label ->
        Ok
        <| LabelAdded
            { NamespaceId = namespaceId
              Name = label.Name
              Value = label.Value
              LabelId = label.LabelId }
    | None -> Error EmptyName

let identify =
    function
    | NewNamespace (boundedContextId, _) -> boundedContextId
    | RemoveNamespace (boundedContextId, _) -> boundedContextId
    | AddLabel (boundedContextId, _, _) -> boundedContextId
    | RemoveLabel (boundedContextId, _) -> boundedContextId

let name identity = identity

let handle (state: State) (command: Command) =
    match command with
    | NewNamespace (boundedContextId, namespaceCommand) ->
        addNewNamespace boundedContextId namespaceCommand.Name namespaceCommand.Template namespaceCommand.Labels state
    | RemoveNamespace (_, namespaceId) ->
        Ok
        <| NamespaceRemoved { NamespaceId = namespaceId }
    | AddLabel (_, namespaceId, labelCommand) -> addLabel namespaceId labelCommand.Name labelCommand.Value
    | RemoveLabel (_, labelCommand) ->
        Ok
        <| LabelRemoved
            { NamespaceId = labelCommand.Namespace
              LabelId = labelCommand.Label }
    |> Result.map List.singleton


module Projections =
    let convertLabels (labels: LabelDefinition list): Label list =
        labels
        |> List.map (fun l ->
            { Name = l.Name
              Id = l.LabelId
              Value = l.Value |> Option.defaultValue null
              Template = l.Template })
        
    let asNamespace namespaceOption event =
        match event with
        | NamespaceImported c ->
            Some {
              Id = c.NamespaceId
              Template = c.NamespaceTemplateId
              Name = c.Name
              Labels = c.Labels |> convertLabels }
        | NamespaceAdded c ->
            Some {
              Id = c.NamespaceId
              Template = c.NamespaceTemplateId
              Name = c.Name
              Labels = c.Labels |> convertLabels }
        | NamespaceRemoved c ->
            None
        | LabelAdded c ->
            namespaceOption
            |> Option.map (fun n ->
                { n with
                      Labels =
                          { Id = c.LabelId
                            Name = c.Name
                            Value = c.Value |> Option.defaultValue null
                            Template = None }
                          :: n.Labels }
            )
        | LabelRemoved c ->
            namespaceOption
            |> Option.map (fun n ->
                { n with
                      Labels =
                          n.Labels
                          |> List.filter (fun l -> l.Id <> c.LabelId) }
            )

    let asNamespaces namespaces event =
        match event with
        | NamespaceImported c ->
            { Id = c.NamespaceId
              Template = c.NamespaceTemplateId
              Name = c.Name
              Labels = c.Labels |> convertLabels }
            :: namespaces
        | NamespaceAdded c ->
            { Id = c.NamespaceId
              Template = c.NamespaceTemplateId
              Name = c.Name
              Labels = c.Labels |> convertLabels }
            :: namespaces
        | NamespaceRemoved c ->
            namespaces
            |> List.filter (fun n -> n.Id <> c.NamespaceId)
        | LabelAdded c ->
            namespaces
            |> List.map (fun n ->
                if n.Id = c.NamespaceId then
                    { n with
                          Labels =
                              { Id = c.LabelId
                                Name = c.Name
                                Value = c.Value |> Option.defaultValue null
                                Template = None }
                              :: n.Labels }
                else
                    n)
        | LabelRemoved c ->
            namespaces
            |> List.map (fun n ->
                if n.Id = c.NamespaceId then
                    { n with
                          Labels =
                              n.Labels
                              |> List.filter (fun l -> l.Id <> c.LabelId) }
                else
                    n)
            
            
    let asNamespaceWithBoundedContext boundedContextOption event =
        boundedContextOption
        |> Option.map (fun boundedContext ->
            { boundedContext with Namespaces = asNamespaces boundedContext.Namespaces event })
