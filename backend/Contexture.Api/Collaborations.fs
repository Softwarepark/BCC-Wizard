﻿namespace Contexture.Api

open Contexture.Api
open Contexture.Api.Aggregates
open Contexture.Api.Database
open Microsoft.AspNetCore.Http
open FSharp.Control.Tasks

open Giraffe

module Collaborations =
    module CommandEndpoints =
        open Collaboration
        open FileBasedCommandHandlers

        let private updateAndReturnCollaboration command =
            fun (next: HttpFunc) (ctx: HttpContext) ->
                task {
                    let database = ctx.GetService<FileBased>()

                    match Collaboration.handle database command with
                    | Ok updatedContext ->
                        let collaboration =
                            updatedContext
                            |> database.Read.Collaborations.ById
                            |> Option.get

                        return! json collaboration next ctx
                    | Error (DomainError EmptyName) ->
                        return! RequestErrors.BAD_REQUEST "Name must not be empty" next ctx
                    | Error e -> return! ServerErrors.INTERNAL_ERROR e next ctx
                }

        let relationshipType collaborationId (command: ChangeRelationshipType) =
            updateAndReturnCollaboration (ChangeRelationshipType(collaborationId, command))
    
    let getCollaborations =
        fun (next : HttpFunc) (ctx : HttpContext) ->
            let database = ctx.GetService<FileBased>()
            let collaborations = database.Read.Collaborations.All
            json collaborations next ctx
            
    let getCollaboration collaborationId =
        fun (next: HttpFunc) (ctx: HttpContext) ->
            let database = ctx.GetService<FileBased>()
            let document = database.Read

            let result =
                collaborationId
                |> document.Collaborations.ById
                |> Option.map json
                |> Option.defaultValue (RequestErrors.NOT_FOUND(sprintf "Collaboration %i not found" collaborationId))

            result next ctx
    
    let routes : HttpHandler =
        subRoute "/collaborations" (
            choose [
                subRoutef "/%i" (fun contextId ->
                    choose [
                        GET >=> getCollaboration contextId
                        POST
                        >=> route "/relationshipType"
                        >=> bindJson (CommandEndpoints.relationshipType contextId)
                    ]
                )
                GET >=> getCollaborations
            ])

