module Domain.Index exposing (Msg, Model, update, view, initWithSubdomains, initWithoutSubdomains)

import Browser.Navigation as Nav

import Json.Encode as Encode
import Json.Decode.Pipeline as JP
import Json.Decode as Decode exposing(Decoder)

import Html exposing (Html, button, div, text)
import Html.Attributes exposing (..)
import Html.Events

import Bootstrap.Grid as Grid
import Bootstrap.Grid.Row as Row
import Bootstrap.Grid.Col as Col
import Bootstrap.Form as Form
import Bootstrap.Form.Fieldset as Fieldset
import Bootstrap.Form.Input as Input
import Bootstrap.Form.InputGroup as InputGroup
import Bootstrap.Button as Button
import Bootstrap.ListGroup as ListGroup
import Bootstrap.Card as Card
import Bootstrap.Card.Block as Block
import Bootstrap.Utilities.Spacing as Spacing

import RemoteData
import Url
import Http

import Bcc
import Domain
import Domain.Create
import Route

-- MODEL

type alias Domain = Domain.Domain

type alias Model =
  { navKey : Nav.Key
  , baseUrl : Url.Url
  , showSubdomains : Bool
  , createDomain: Domain.Create.Model
  , domains: RemoteData.WebData (List Domain)
   }

init: Bool -> Url.Url -> Nav.Key -> (Model, Cmd Msg)
init subDomains baseUrl key =
  let
    (createModel, createCmd) = Domain.Create.init baseUrl key
  in
  ( { navKey = key
    , baseUrl = baseUrl
    , showSubdomains = subDomains
    , createDomain = createModel
    , domains = RemoteData.Loading }
  , Cmd.batch [loadAll baseUrl, createCmd |> Cmd.map CreateMsg] )

initWithSubdomains : Url.Url -> Nav.Key -> (Model, Cmd Msg)
initWithSubdomains baseUrl key =
  init True baseUrl key

initWithoutSubdomains : Url.Url -> Nav.Key -> (Model, Cmd Msg)
initWithoutSubdomains baseUrl key =
  init False baseUrl key


-- UPDATE

type Msg
  = Loaded (Result Http.Error (List Domain))
  | CreateMsg Domain.Create.Msg

update : Msg -> Model -> (Model, Cmd Msg)
update msg model =
  case msg of
    Loaded (Ok items) ->
      let
        filtered =
          items
          |> List.filter (\i ->
            case i.parentDomain of 
              Just _ -> model.showSubdomains
              Nothing -> not model.showSubdomains
            )
      in ({ model | domains = RemoteData.Success filtered }, Cmd.none)
    Loaded (Err e) ->
        ({ model | domains = RemoteData.Failure e }, Cmd.none)
    CreateMsg create ->
      let
        (createModel, createCmd) = Domain.Create.update create model.createDomain
      in
        ({ model | createDomain = createModel }, createCmd |> Cmd.map CreateMsg)

-- VIEW

viewDomain : Domain -> Card.Config Msg
viewDomain item =
  Card.config []
    |> Card.headerH4 [] [ text item.name ]
    |> Card.block []
      ( if String.length item.vision > 0
        then [ Block.text [] [ text item.vision  ] ]
        else []
      )
    |> Card.footer []
      [ Html.a
        [ href (Route.routeToString (Route.Domain item.id)), class "stretched-link" ]
        [ text "View Domain" ]
      ]

viewExisting : List Domain  -> Html Msg
viewExisting items =
  if List.isEmpty items then
    Html.p
        [ class "lead" ]
        [ text "No existing domains found - do you want to create one?" ]
  else
    Card.deck (items |> List.map viewDomain)

view : Model -> Html Msg
view model =
  let
    details =
      case model.domains of
        RemoteData.Success items ->
          [ Grid.row [ Row.attrs [ Spacing.pt3 ] ]
              [ Grid.col [] [viewExisting items ] ]
          , Grid.row [ Row.attrs [ Spacing.mt3 ] ]
              [ Grid.col []
                [ model.createDomain |> Domain.Create.view |> Html.map CreateMsg ]
              ]
          ]
        _ ->
          [ Grid.row []
              [ Grid.col [] [ text "Loading your domains"] ]
          ]
  in
    if model.showSubdomains
    then div [] details
    else Grid.container [] details


-- helpers

loadAll: Url.Url -> Cmd Msg
loadAll baseUrl =
  Http.get
    { url = Url.toString { baseUrl | path = baseUrl.path ++ "/domains" }
    , expect = Http.expectJson Loaded Domain.domainsDecoder
    }


