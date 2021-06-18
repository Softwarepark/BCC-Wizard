namespace Contexture.Api.ReadModels

open System
open Contexture.Api
open Contexture.Api.Aggregates.Namespace
open Contexture.Api.Infrastructure
open Entities

module Find =
    type Operator =
        | Equals
        | StartsWith
        | Contains
        | EndsWith

    type SearchPhrase = private | SearchPhrase of Operator * string

    type SearchTerm = private SearchTerm of string

    module SearchTerm =
        let fromInput (term: string) =
            term
            |> Option.ofObj
            |> Option.filter (not << String.IsNullOrWhiteSpace)
            |> Option.map (fun s -> s.Trim())
            |> Option.map SearchTerm

        let value (SearchTerm term) = term

    module SearchPhrase =

        let private operatorAndPhrase (phrase: string) =
            match phrase.StartsWith "*", phrase.EndsWith "*" with
            | true, true -> // *phrase*
                Contains, phrase.Trim '*'
            | true, false -> // *phrase
                EndsWith, phrase.TrimStart '*'
            | false, true -> // phrase*
                StartsWith, phrase.TrimEnd '*'
            | false, false -> // phrase
                Equals, phrase

        let fromInput (phrase: string) =
            phrase
            |> Option.ofObj
            |> Option.filter (not << String.IsNullOrWhiteSpace)
            |> Option.map (fun s -> s.Trim())
            |> Option.map operatorAndPhrase
            |> Option.map SearchPhrase

        let matches (SearchPhrase (operator, phrase)) (SearchTerm value) =
            match operator with
            | Equals -> String.Equals(phrase, value, StringComparison.OrdinalIgnoreCase)
            | StartsWith -> value.StartsWith(phrase, StringComparison.OrdinalIgnoreCase)
            | EndsWith -> value.EndsWith(phrase, StringComparison.OrdinalIgnoreCase)
            | Contains -> value.Contains(phrase, StringComparison.OrdinalIgnoreCase)

    let private appendToSet items (key, value) =
        items
        |> Map.change
            key
            (function
            | Some values -> values |> Set.add value |> Some
            | None -> value |> Set.singleton |> Some)

    let private removeFromSet findValue value items =
        items
        |> Map.map
            (fun _ (values: Set<_>) ->
                values
                |> Set.filter (fun n -> findValue n <> value))

    let private findByKey keyPhrase items =
        let matchesKey (key: string) =
            let term = key |> SearchTerm.fromInput

            term
            |> Option.map (SearchPhrase.matches keyPhrase)
            |> Option.defaultValue false

        items
        |> Map.filter (fun k _ -> matchesKey k)
        |> Map.toList
        |> List.map snd

    let private findByKeys keyPhrases items =
        // TODO: could this functionality be reused?
        if keyPhrases |> Seq.isEmpty then
            None
        else
            keyPhrases
            |> Seq.collect (fun phrase -> findByKey phrase items)
            |> Set.ofSeq
            |> Some

    let private selectResults selectResult items =
        items
        |> Option.ofObj
        |> Option.filter (not << Seq.isEmpty)
        |> Option.map (Seq.map (Set.map selectResult))

    let takeAllResults items = items |> Option.map Set.unionMany

    let combineResults items = items |> Option.map Set.intersectMany

    module Namespaces =
        type NamespaceModel =
            { NamespaceId: NamespaceId
              NamespaceTemplateId: NamespaceTemplateId option }

        type NamespaceFinder = Map<string, Set<NamespaceModel>>

        let projectNamespaceNameToNamespaceId state eventEnvelope =
            match eventEnvelope.Event with
            | NamespaceAdded n ->
                appendToSet
                    state
                    (n.Name,
                     { NamespaceId = n.NamespaceId
                       NamespaceTemplateId = n.NamespaceTemplateId })
            | NamespaceImported n ->
                appendToSet
                    state
                    (n.Name,
                     { NamespaceId = n.NamespaceId
                       NamespaceTemplateId = n.NamespaceTemplateId })
            | NamespaceRemoved n ->
                state
                |> removeFromSet (fun i -> i.NamespaceId) n.NamespaceId
            | LabelAdded l -> state
            | LabelRemoved l -> state

        let byName (namespaces: NamespaceFinder) (name: SearchPhrase seq) =
            namespaces |> findByKeys name |> combineResults

        let byTemplate (namespaces: NamespaceFinder) (templateId: NamespaceTemplateId) =
            namespaces
            |> Map.toList
            |> List.map snd
            |> Set.unionMany
            |> Set.filter (fun m -> m.NamespaceTemplateId = Some templateId)

    let namespaces (eventStore: EventStore) : Namespaces.NamespaceFinder =
        eventStore.Get<Aggregates.Namespace.Event>()
        |> List.fold Namespaces.projectNamespaceNameToNamespaceId Map.empty

    module Labels =
        type LabelAndNamespaceModel =
            { Value: string option
              NamespaceId: NamespaceId
              NamespaceTemplateId: NamespaceTemplateId option }

        type NamespacesByLabel =
            { ByLabelName2: Map<String, NamespaceMap>
              ByLabelValue:  Map<String, NamespaceMap> }
            static member Initial =
                {
                  // TODO: group by BC ID
                  ByLabelName2 = Map.empty
                  ByLabelValue = Map.empty }
        and NamespaceMap = Map<BoundedContextId, Set<NamespaceId * LabelId>>

        let projectLabelNameToNamespace state eventEnvelope =
            match eventEnvelope.Event with
            | NamespaceAdded n ->
                { state with
                      ByLabelName2 =
                          n.Labels
                         |> List.fold
                              (fun map l ->
                                  map
                                  |> Map.change
                                      l.Name
                                      (fun items ->
                                          match items with
                                          | Some i ->
                                              Some(appendToSet i (n.BoundedContextId, (n.NamespaceId, l.LabelId)))
                                          | None ->
                                              Some(
                                                  appendToSet Map.empty (n.BoundedContextId, (n.NamespaceId, l.LabelId))
                                              )))
                              state.ByLabelName2


                      ByLabelValue =
                          n.Labels
                          |> List.choose
                              (fun l ->
                                  l.Value
                                  |> Option.map (fun value -> value, (n.NamespaceId, l.LabelId)))
                          |> List.fold (fun map (key,value) ->
                                  map
                                  |> Map.change
                                      key
                                      (fun items ->
                                          match items with
                                          | Some i ->
                                              Some(appendToSet i (n.BoundedContextId, value))
                                          | None ->
                                              Some(
                                                  appendToSet Map.empty (n.BoundedContextId, value)
                                              )))
                                state.ByLabelValue }
            | NamespaceImported n ->
                { state with
                      ByLabelName2 =
                          n.Labels
                         |> List.fold
                              (fun map l ->
                                  map
                                  |> Map.change
                                      l.Name
                                      (fun items ->
                                          match items with
                                          | Some i ->
                                              Some(appendToSet i (n.BoundedContextId, (n.NamespaceId, l.LabelId)))
                                          | None ->
                                              Some(
                                                  appendToSet Map.empty (n.BoundedContextId, (n.NamespaceId, l.LabelId))
                                              )))
                              state.ByLabelName2
                      ByLabelValue =
                         n.Labels
                          |> List.choose
                              (fun l ->
                                  l.Value
                                  |> Option.map (fun value -> value, (n.NamespaceId, l.LabelId)))
                          |> List.fold (fun map (key,value) ->
                                  map
                                  |> Map.change
                                      key
                                      (fun items ->
                                          match items with
                                          | Some i ->
                                              Some(appendToSet i (n.BoundedContextId, value))
                                          | None ->
                                              Some(
                                                  appendToSet Map.empty (n.BoundedContextId, value)
                                              )))state.ByLabelValue }
            | LabelAdded l ->
                { state with
                      ByLabelName2 =
                          state.ByLabelName2
                          |> Map.change
                              l.Name
                              (fun s ->
                                  match s with
                                  | Some byBc ->
                                      byBc
                                      |> Map.map
                                          (fun k v ->
                                              if v
                                                 |> Set.exists (fun (namespaceId, _) -> namespaceId = l.NamespaceId) then
                                                  Set.add (l.NamespaceId, l.LabelId) v
                                              else
                                                  v)
                                      |> Some
                                  | None -> None)
                      ByLabelValue =
                          match l.Value with
                          | Some value ->
                            state.ByLabelValue
                            |> Map.change
                              l.Name
                              (fun s ->
                                  match s with
                                  | Some byBc ->
                                      byBc
                                      |> Map.map
                                          (fun k v ->
                                              if v
                                                 |> Set.exists (fun (namespaceId, _) -> namespaceId = l.NamespaceId) then
                                                  Set.add (l.NamespaceId, l.LabelId) v
                                              else
                                                  v)
                                      |> Some
                                  | None -> None
                            )
                          | None -> state.ByLabelValue }
            | LabelRemoved l ->
                { state with
                      ByLabelName2 =
                          state.ByLabelName2
                          |> Map.map (fun _ values -> values |> removeFromSet fst l.NamespaceId)
                      ByLabelValue =
                          state.ByLabelValue
                          |> Map.map (fun _ values -> values |> removeFromSet snd l.NamespaceId)
                          }
            | NamespaceRemoved n ->
                { state with
                      ByLabelName2 =
                          state.ByLabelName2
                          |> Map.map (fun _ values -> values |> removeFromSet fst n.NamespaceId)
                      ByLabelValue =
                          state.ByLabelValue
                          |> Map.map (fun _ values -> values |> removeFromSet snd n.NamespaceId) }

        let byLabelName (namespaces: NamespacesByLabel) (phrase: SearchPhrase) =
            namespaces.ByLabelName2
            |> findByKey phrase
            |> List.map (Map.toList >> List.map fst >> Set.ofList)
//            |> selectResults id 
//            |> takeAllResults
            |> Set.unionMany

        let byLabelValue (namespaces: NamespacesByLabel) (phrase: SearchPhrase) =
            namespaces.ByLabelValue
            |> findByKey phrase
            |> List.map (Map.toList >> List.map fst >> Set.ofList)
//            |> selectResults id 
//            |> takeAllResults
            |> Set.unionMany

    let labels (eventStore: EventStore) : Labels.NamespacesByLabel =
        eventStore.Get<Aggregates.Namespace.Event>()
        |> List.fold Labels.projectLabelNameToNamespace Labels.NamespacesByLabel.Initial

    module Domains =
        open Contexture.Api.Aggregates.Domain

        type DomainByKeyAndNameModel =
            { ByKey: Map<string, DomainId>
              ByName: Map<string, Set<DomainId>> }
            static member Empty =
                { ByKey = Map.empty
                  ByName = Map.empty }

        let projectToDomain state eventEnvelope =
            let addKey canBeKey domain byKey =
                match canBeKey with
                | Some key -> byKey |> Map.add key domain
                | None -> byKey

            let append key value items = appendToSet items (key, value)

            match eventEnvelope.Event with
            | SubDomainCreated n ->
                { state with
                      ByName = state.ByName |> append n.Name n.DomainId }
            | DomainCreated n ->
                { state with
                      ByName = state.ByName |> append n.Name n.DomainId }
            | KeyAssigned k ->
                { state with
                      ByKey =
                          state.ByKey
                          |> Map.filter (fun _ v -> v <> k.DomainId)
                          |> addKey k.Key k.DomainId }
            | DomainImported n ->
                { state with
                      ByName = appendToSet state.ByName (n.Name, n.DomainId)
                      ByKey = state.ByKey |> addKey n.Key n.DomainId }
            | DomainRenamed l ->
                { state with
                      ByName =
                          state.ByName
                          |> removeFromSet id l.DomainId
                          |> append l.Name l.DomainId }
            | DomainRemoved l ->
                { state with
                      ByName = state.ByName |> removeFromSet id l.DomainId
                      ByKey =
                          state.ByKey
                          |> Map.filter (fun _ v -> v <> l.DomainId) }
            | CategorizedAsSubdomain _
            | PromotedToDomain _
            | VisionRefined _ -> state

        let byName (model: DomainByKeyAndNameModel) (phrase: SearchPhrase seq) =
            model.ByName
            |> findByKeys phrase
            |> combineResults

        let byKey (model: DomainByKeyAndNameModel) (phrase: SearchPhrase seq) = model.ByKey |> findByKeys phrase

    let domains (eventStore: EventStore) : Domains.DomainByKeyAndNameModel =
        eventStore.Get<Aggregates.Domain.Event>()
        |> List.fold Domains.projectToDomain Domains.DomainByKeyAndNameModel.Empty


    module BoundedContexts =
        open Contexture.Api.Aggregates.BoundedContext

        type BoundedContextByKeyAndNameModel =
            { ByKey: Map<string, BoundedContextId>
              ByName: Map<string, Set<BoundedContextId>> }
            static member Empty =
                { ByKey = Map.empty
                  ByName = Map.empty }

        let projectToBoundedContext state eventEnvelope =
            let addKey canBeKey domain byKey =
                match canBeKey with
                | Some key -> byKey |> Map.add key domain
                | None -> byKey

            let append key value items = appendToSet items (key, value)

            match eventEnvelope.Event with
            | BoundedContextCreated n ->
                { state with
                      ByName = state.ByName |> append n.Name n.BoundedContextId }
            | KeyAssigned k ->
                { state with
                      ByKey =
                          state.ByKey
                          |> Map.filter (fun _ v -> v <> k.BoundedContextId)
                          |> addKey k.Key k.BoundedContextId }
            | BoundedContextImported n ->
                { state with
                      ByName = appendToSet state.ByName (n.Name, n.BoundedContextId)
                      ByKey = state.ByKey |> addKey n.Key n.BoundedContextId }
            | BoundedContextRenamed l ->
                { state with
                      ByName =
                          state.ByName
                          |> removeFromSet id l.BoundedContextId
                          |> append l.Name l.BoundedContextId }
            | BoundedContextRemoved l ->
                { state with
                      ByName =
                          state.ByName
                          |> removeFromSet id l.BoundedContextId
                      ByKey =
                          state.ByKey
                          |> Map.filter (fun _ v -> v <> l.BoundedContextId) }
            | _ -> state

        let byName (model: BoundedContextByKeyAndNameModel) (phrase: SearchPhrase seq) =
            model.ByName
            |> findByKeys phrase
            |> combineResults

        let byKey (model: BoundedContextByKeyAndNameModel) (phrase: SearchPhrase seq) = model.ByKey |> findByKeys phrase

    let boundedContexts (eventStore: EventStore) : BoundedContexts.BoundedContextByKeyAndNameModel =
        eventStore.Get<Aggregates.BoundedContext.Event>()
        |> List.fold BoundedContexts.projectToBoundedContext BoundedContexts.BoundedContextByKeyAndNameModel.Empty
