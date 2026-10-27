module FakeGriffin

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open Griffin

type Call =
    { Method: string
      /// Path and query as sent.
      Target: string
      Body: JsonElement voption
      BodyBytes: byte[]
      Headers: Map<string, string>
      ContentHeaders: Map<string, string> }

/// Answers each request with respond method target body, and records the calls.
type FakeGriffinHandler(respond: string -> string -> JsonElement voption -> int * string) =
    inherit HttpMessageHandler()
    let mutable calls: Call list = []

    member _.Calls = List.rev calls

    override _.SendAsync(request, ct) =
        task {
            ct.ThrowIfCancellationRequested()

            let! bytes =
                if isNull request.Content then
                    Threading.Tasks.Task.FromResult Array.empty
                else
                    request.Content.ReadAsByteArrayAsync()

            let body =
                if bytes.Length = 0 then
                    ValueNone
                else
                    ValueSome(JsonDocument.Parse(bytes).RootElement.Clone())

            let headersOf (hs: Headers.HttpHeaders) =
                hs |> Seq.map (fun h -> h.Key, String.Join(",", h.Value)) |> Map.ofSeq

            let target = request.RequestUri.PathAndQuery
            let method = request.Method.Method

            calls <-
                { Method = method
                  Target = target
                  Body = body
                  BodyBytes = bytes
                  Headers = headersOf request.Headers
                  ContentHeaders =
                    if isNull request.Content then Map.empty else headersOf request.Content.Headers }
                :: calls

            let status, json = respond method target body
            let response = new HttpResponseMessage(enum<HttpStatusCode> status)

            if json <> "" then
                response.Content <- new StringContent(json, Encoding.UTF8, "application/json")

            return response
        }

let config = GriffinConfig.create "g-test-key-1"

let clientWith (handler: FakeGriffinHandler) =
    { Config = config
      Http = new HttpClient(handler) }

let ok json = 200, json

let created json = 201, json

let griffinError status (title: string) (pointer: string) =
    status,
    $"""{{"errors":[{{"title":"{title}","status":"{status}","detail":"Detail of {title}","code":"some-code","source":{{"pointer":"{pointer}"}},"links":{{"about":"https://docs.griffin.com/docs/errors/x"}}}}]}}"""

let organizationJson =
    """{"display-name":"Test Org","organization-mode":"test-mode","own-legal-person-url":"/legal-persons/lp.own","organization-legal-persons-url":"/organizations/og.1/legal-persons","organization-bank-accounts-url":"/organizations/og.1/bank/accounts","organization-bank-account-products-url":"/organizations/og.1/bank/products","organization-workflows-url":"/organizations/og.1/workflows","organization-payments-url":"/organizations/og.1/payments","organization-events-url":"/organizations/og.1/events","organization-webhooks-url":"/organizations/og.1/webhooks"}"""

let accountJson (url: string) (status: string) =
    $"""{{"account-url":"{url}","display-name":"Account","account-status":"{status}","bank-product-type":"embedded-account","account-balance":{{"currency":"GBP","value":"100.00"}},"available-balance":{{"currency":"GBP","value":"90.50"}},"bank-addresses":[{{"account-holder":"Ada","bank-id":"000001","bank-id-code":"gbdsc","account-number":"00330292","account-number-code":"bban"}}],"owner-url":"/legal-persons/lp.1","account-payments-url":"{url}/payments","account-transactions-url":"{url}/transactions","close-account-url":"{url}/actions/close","account-admissions-url":"{url}/admissions","account-submissions-url":"{url}/submissions","bank-account-restrictions-url":"{url}/restrictions","account-statements-url":"{url}/statements","account-mandates-url":"{url}/mandates","pooled-funds":false,"account-restricted":false,"created-at":"2026-10-26T16:46:13.725Z"}}"""

let transactionJson (id: string) (value: string) (direction: string) =
    $"""{{"account-transaction-url":"/bank/transactions/{id}","account-url":"/bank/accounts/ba.1","balance-change":{{"currency":"GBP","value":"{value}"}},"balance-change-direction":"{direction}","account-balance":{{"currency":"GBP","value":"1000.00"}},"transaction-origin-type":"payment","reference":"ref","description":"Payment to Ada","payment-url":"/payments/pm.1","effective-at":"2026-10-26T16:42:00.532Z","post-datetime":"2026-10-26T16:42:00.935Z"}}"""

let pageJson (key: string) (items: string list) (next: string voption) =
    let nextJson =
        match next with
        | ValueSome n -> $"\"{n}\""
        | ValueNone -> "null"

    $"""{{"{key}":[{String.Join(",", items)}],"links":{{"prev":null,"next":{nextJson}}}}}"""

let paymentJson =
    """{"payment-url":"/payments/pm.1","payment-direction":"outbound-payment","payment-amount":{"currency":"GBP","value":"25.50"},"payment-reference":"Rent","payment-submissions-url":"/payments/pm.1/submissions","created-at":"2026-10-26T16:42:00.532Z"}"""

let submissionJson (status: string) =
    $"""{{"submission-url":"/submissions/ps.1","payment-url":"/payments/pm.1","account-url":"/bank/accounts/ba.1","submission-status":"{status}","submission-scheme-information":{{"payment-scheme":"fps","end-to-end-identification":"e2e-1","scheme-status-code":"0000"}},"created-at":"2026-10-26T16:42:01Z"}}"""
