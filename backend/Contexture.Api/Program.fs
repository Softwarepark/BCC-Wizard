module Contexture.Api.App

open System
open System.IO
open Contexture.Api.Aggregates
open Contexture.Api.Database
open Contexture.Api.Infrastructure
open Contexture.Api.FileBasedCommandHandlers
open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Cors.Infrastructure
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open FSharp.Control.Tasks

[<CLIMutable>]
type ContextureOptions = 
    { DatabasePath: string 
      GitHash: string
    }

module AllRoute =

    let getAllData =
        fun (next: HttpFunc) (ctx: HttpContext) -> task {
            let database = ctx.GetService<SingleFileBasedDatastore>()
            let! document = database.Read()

            let result =
                {| BoundedContexts = document.BoundedContexts.All
                   Domains = document.Domains.All
                   Collaborations = document.Collaborations.All
                   NamespaceTemplates = document.NamespaceTemplates.All |}

            return! json result next ctx
        }

    [<CLIMutable>]
    type UpdateAllData =
        { Domains: Serialization.Domain list
          BoundedContexts: Serialization.BoundedContext list
          Collaborations: Serialization.Collaboration list
          NamespaceTemplates: NamespaceTemplate.Projections.NamespaceTemplate list }

    let putReplaceAllData =
        fun (next: HttpFunc) (ctx: HttpContext) ->
            task {
                let database =
                    ctx.GetService<SingleFileBasedDatastore>()

                let logger = ctx.GetLogger()
                let notEmpty items = not (List.isEmpty items)
                let! data = ctx.BindJsonAsync<UpdateAllData>()

                let doNotReturnOldData =
                    ctx.TryGetQueryStringValue("doNotReturnOldData")
                    |> Option.map (fun value -> value.ToLowerInvariant() = "true")
                    |> Option.defaultValue false

                if notEmpty data.Domains
                   && notEmpty data.BoundedContexts
                   && notEmpty data.Collaborations then
                    logger.LogWarning(
                        "Replacing stored data with {Domains}, {BoundedContexts}, {Collaborations}, {NamespaceTemplates}",
                        data.Domains.Length,
                        data.BoundedContexts.Length,
                        data.Collaborations.Length,
                        data.NamespaceTemplates.Length
                    )

                    let! oldDocument = database.Read()

                    let! result =
                        database.Change
                            (fun _ ->
                                let newDocument : Document =
                                    { Domains = collectionOfGuid data.Domains (fun d -> d.Id)
                                      BoundedContexts = collectionOfGuid data.BoundedContexts (fun d -> d.Id)
                                      Collaborations = collectionOfGuid data.Collaborations (fun d -> d.Id)
                                      NamespaceTemplates = collectionOfGuid data.NamespaceTemplates (fun d -> d.Id) }

                                Ok newDocument)

                    match result with
                    | Ok _ ->
                        let lifetime =
                            ctx.GetService<IHostApplicationLifetime>()

                        logger.LogInformation("Stopping Application after reseeding of data")
                        lifetime.StopApplication()

                        if doNotReturnOldData then
                            return!
                                text
                                    "Successfully imported all data - NOTE: an application shutdown was initiated!"
                                    next
                                    ctx
                        else
                            return!
                                json
                                    {| Message =
                                           "Successfully imported all data - NOTE: an application shutdown was initiated!"
                                       OldData = oldDocument |}
                                    next
                                    ctx
                    | Error e -> return! ServerErrors.INTERNAL_ERROR $"Could not import document: %s{e}" next ctx
                else
                    return! RequestErrors.BAD_REQUEST "Not overwriting with (partly) missing data" next ctx
            }

    let routes =
        route "/all"
        >=> choose [ GET >=> getAllData
                     PUT >=> putReplaceAllData ]


let status : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) ->
        let env = ctx.GetService<IOptions<ContextureOptions>>()
        match env.Value.GitHash with
        | hash when not (String.IsNullOrEmpty hash) ->
            json {| GitHash = hash |} next ctx
        | _ ->
            text "No status information" next ctx

let webApp hostFrontend =
    choose [
         subRoute "/api"
             (choose [
                   Domains.routes
                   BoundedContexts.routes
                   Collaborations.routes
                   Namespaces.routes
                   Search.apiRoutes
                   AllRoute.routes
                   RequestErrors.NOT_FOUND "Not found"
            ])
         route "/meta" >=> GET >=> status
         Search.routes
         GET
         >=> routef "/boundedContext/%O/namespaces" Namespaces.index
         hostFrontend
         setStatusCode 404 >=> text "Not Found"
    ]

let frontendHostRoutes (env: IWebHostEnvironment) : HttpHandler =
    let detectRedirectLoop : HttpHandler =
        fun (next : HttpFunc) (ctx : HttpContext) ->
            let headers = HeaderDictionaryTypeExtensions.GetTypedHeaders(ctx.Request)
            match headers.Referer |> Option.ofObj with
            | Some referer when referer.AbsolutePath = ctx.Request.Path.ToUriComponent() && referer.Query = ctx.Request.QueryString.ToUriComponent() ->
                RequestErrors.NOT_FOUND "Not found and stuck in a redirect loop" next ctx
            | _ ->
                next ctx
    if env.IsDevelopment() then
        detectRedirectLoop >=>
            choose [
                GET >=> 
                    fun (next : HttpFunc) (ctx : HttpContext) -> 
                        let urlBuilder =
                            ctx.GetRequestUrl()
                            |> UriBuilder
                        urlBuilder.Port <- 8000
                        urlBuilder.Scheme <- "http"
                        redirectTo false (urlBuilder.ToString()) next ctx
            ]
     
    else
        detectRedirectLoop >=>
            choose [
                route "/" >=> htmlFile "wwwroot/index.html"
                GET >=> htmlFile "wwwroot/index.html"
            ]

let errorHandler (ex : Exception) (logger : ILogger) =
    logger.LogError(ex, "An unhandled exception has occurred while executing the request.")
    clearResponse >=> setStatusCode 500 >=> text ex.Message

let clock =
    fun () ->  System.DateTime.UtcNow
        
let configureCors (builder : CorsPolicyBuilder) =
    builder
        .AllowAnyOrigin()
        .AllowAnyMethod()
        .AllowAnyHeader()
        |> ignore

let configureApp (app : IApplicationBuilder) =    
    let env = app.ApplicationServices.GetService<IWebHostEnvironment>()
    (match env.IsDevelopment() with
    | true  ->
        app.UseDeveloperExceptionPage()
    | false ->
        app.UseGiraffeErrorHandler(errorHandler))
        .UseCors(configureCors)
        .UseStaticFiles()
        .UseGiraffe(webApp (frontendHostRoutes env))
        
let configureJsonSerializer (services: IServiceCollection) =
    Database.Serialization.serializerOptions
    |> SystemTextJson.Serializer
    |> services.AddSingleton<Json.ISerializer>
    |> ignore
    
let registerReadModel<'R, 'E, 'S when 'R :> ReadModels.ReadModel<'E,'S> and 'R : not struct> (readModel: 'R) (services: IServiceCollection) =
    services.AddSingleton<'R>(readModel) |> ignore
    let initializeReadModel (s: IServiceProvider) =
        ReadModels.ReadModelInitialization.initializeWith (s.GetRequiredService<EventStore>()) readModel.EventHandler
    services.AddSingleton<ReadModels.ReadModelInitialization> initializeReadModel
    
let configureReadModels (services: IServiceCollection) =
    services
    |> registerReadModel (ReadModels.Domain.domainsReadModel())
    |> registerReadModel (ReadModels.Collaboration.collaborationsReadModel())
    |> registerReadModel (ReadModels.Templates.templatesReadModel())
    |> registerReadModel (ReadModels.BoundedContext.boundedContextsReadModel())
    |> ignore

let configureServices (context: HostBuilderContext) (services : IServiceCollection) =
    services
        .AddOptions<ContextureOptions>()
        .Bind(context.Configuration)
        .Validate((fun options -> not (String.IsNullOrEmpty options.DatabasePath)), "A non-empty DatabasePath configuration is required")
        |> ignore
    services.AddSingleton<SingleFileBasedDatastore>(fun services ->
        let options = services.GetRequiredService<IOptions<ContextureOptions>>()
        // TODO: danger zone - loading should not be done as part of the initialization
        AgentBased.initializeDatabase(options.Value.DatabasePath)
        |> Async.AwaitTask
        |> Async.RunSynchronously
        )
        
        |> ignore
    
    services.AddSingleton<Clock>(clock) |> ignore
    services.AddSingleton<EventStore> (EventStore.Empty) |> ignore 
    services |> configureReadModels
    
    services.AddCors() |> ignore
    services.AddGiraffe() |> ignore
    services |> configureJsonSerializer

let configureLogging (builder : ILoggingBuilder) =
    builder.AddConsole()
           .AddDebug() |> ignore

let buildHost args =
    Host.CreateDefaultBuilder(args)
        .ConfigureServices(configureServices)
        .ConfigureWebHostDefaults(
            fun webHostBuilder ->
                webHostBuilder
                    .Configure(Action<IApplicationBuilder> configureApp)
                    .ConfigureLogging(configureLogging)
                    |> ignore)
        .Build()

let connectAndReplayReadModels (readModels: ReadModels.ReadModelInitialization seq) =
    readModels
    |> Seq.map (fun r -> r.ReplayAndConnect())
    |> Async.Sequential
    |> Async.Ignore


let importFromDocument (store: EventStore) (database: Document) = async {
    let runAsync (items: Async<Unit> list) =
        items
        |> Async.Sequential
        |> Async.Ignore
        
    let clock = fun () -> System.DateTime.UtcNow
    do!
        database.Collaborations.All
        |> List.map (Collaboration.asEvents clock)
        |> List.map store.Append
        |> runAsync
    
    do!
        database.Domains.All
        |> List.map (Domain.asEvents clock)
        |> List.map store.Append
        |> runAsync
    
    do!
        database.BoundedContexts.All
        |> List.map (BoundedContext.asEvents clock)
        |> List.map store.Append
        |> runAsync
    
    do!
        database.BoundedContexts.All
        |> List.map (Namespace.asEvents clock)
        |> List.map store.Append
        |> runAsync

    do!
        database.NamespaceTemplates.All
        |> List.map (NamespaceTemplate.asEvents clock)
        |> List.map store.Append
        |> runAsync
    }
let runAsync (host: IHost) =
    task {
        // make sure the database is loaded
        let database =
            host.Services.GetRequiredService<SingleFileBasedDatastore>()

        let store =
            host.Services.GetRequiredService<EventStore>()

        // Do replays before we import the document
        let readModels =
            host.Services.GetServices<ReadModels.ReadModelInitialization>()

        do! connectAndReplayReadModels readModels

        let! document = database.Read()
        do! importFromDocument store document
        
        let loggerFactory = host.Services.GetRequiredService<ILoggerFactory>()
        let subscriptionLogger = loggerFactory.CreateLogger("subscriptions")

        // subscriptions for syncing back to the filebased-db are added after initial seeding/loading
        store.Subscribe(Collaboration.subscription subscriptionLogger database)
        store.Subscribe(Domain.subscription subscriptionLogger database)
        store.Subscribe(BoundedContext.subscription subscriptionLogger database)
        store.Subscribe(Namespace.subscription subscriptionLogger database)
        store.Subscribe(NamespaceTemplate.subscription subscriptionLogger database)

        return! host.RunAsync()
    }

[<EntryPoint>]
let main args =
    let host = buildHost args
    let executingHost = runAsync host
    executingHost.GetAwaiter().GetResult()
    0