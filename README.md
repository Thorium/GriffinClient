# GriffinClient

A standalone F# client for [Griffin](https://griffin.com), the UK banking-as-a-service platform. It has no
dependencies beyond FSharp.Core and System.Text.Json, and targets `netstandard2.0`, `net8.0` and `net10.0`.
It speaks Griffin API version `2026-07-09` (sent as `Griffin-Version`).

What it covers:

- Navigation: index, organization, bank products, onboarding workflows
- Onboarding: legal persons (create, read, list, rename) with claims (`Claims` module; list, add, delete),
  verifications, the ID document and selfie check link (`IdvCheck`), decisions with decline reasons, and manual
  decisions on referred verifications
- Banking: open, read, list and close bank accounts; freezes and blocks (restrictions); statements (request,
  read, download); Direct Debit mandates (list, read, cancel)
- Payments: Confirmation of Payee (plus its sandbox simulation), payees (create, read, list, deactivate), payments
  (create, submit, submissions), inbound payments (admissions), transactions
- Events with typed payloads, webhook management, and webhook verification (ES512 message signatures) with a key cache
- Request signing (RFC 9421, Ed25519) through a pluggable signer, which live mode requires
- Search for legal persons (by name), bank accounts (by account number) and payments (by reference); rename
  accounts and webhooks
- Companies House company lookup (directors, persons with significant control, address, SIC codes), card holds,
  pooled account membership, open banking consents registered for tell.money, the `verified-bank-account` claim
- Pagination (`Api.allPagesAsync`) and `Api.sendAsync` / `sendRawAsync` for any other endpoint

Not typed (reach them through `Api.sendAsync`): corporate onboarding claims (directors, persons with significant
control, business details), Reliance onboarding, payment updates, team and API key administration.

Griffin offers no credit checks or credit files: those come from a credit reference agency (Experian, Equifax,
TransUnion). For a customer's account data (balances, transactions) use open banking, e.g. PlaidClient.
`Claims.verifiedBankAccount` adds an outside UK account that onboarding checks against the customer, and
`Api.confirmPayeeAsync` checks a name against an account before paying it.

Registering a tell.money consent needs a real tell.money consent and gateway id: made-up ids pass Griffin's
validation and are then refused with a 400 "Unexpected error". The request shape is tested, the live flow is not.

Every call returns `Task<Result<_, GriffinFailure>>`. Griffin's `errors` array comes back as
`ApiError(status, errors)`, each error with its title, detail, code, field pointer and docs link. A request that
cannot be valid (a sort code that is not 6 digits, an unusable `BaseUrl` or signature lifetime) is `InvalidRequest`
and is not sent. Cancelling your token throws `OperationCanceledException` as usual.

Griffin's closed sets of values are DUs: statuses (`AccountStatus`, `SubmissionStatus`, `VerificationStatus`, ...),
outcomes, reasons, schemes and directions. Each has `.Value` (Griffin's string) and `Parse`, and an `Other of string`
case, so a value Griffin adds later still parses. Open-ended codes (decline reason codes, product classes, claim
fields such as industries) stay strings.

## Flow

Griffin is navigated by links: every resource carries the URLs of related resources, so functions take those
URLs rather than ids.

```fsharp
open Griffin

let client = { Config = GriffinConfig.create apiKey; Http = httpClient }   // you own the HttpClient

// 1. Your organization, its products and workflows.
let! org = Api.getOwnOrganizationAsync client ct
let! products = Api.listBankProductsAsync client org.BankProductsUrl ct
let! workflows = Api.listWorkflowsAsync client org.WorkflowsUrl ct

// 2. Onboard a customer: a legal person with the claims the workflow requires, then a verification.
let! person = Api.createLegalPersonAsync client org.LegalPersonsUrl "Ada Example" LegalPersonType.Individual claims ct
let! verification = Api.createVerificationAsync client person.VerificationsUrl.Value workflow.Url ct
// Poll Api.getLegalPersonAsync (or listen for decision-created) until LatestDecision is set.

// 3. Open an account once the decision is Accepted; it is usable when its Status is AccountStatus.Open.
let! account = Api.openBankAccountAsync client product.AccountsUrl "Ada's account" person.Url (ValueSome person.Url) ct

// 4. Pay: create the payment, then submit it.
let bob = { AccountHolder = "Bob Smith"; SortCode = "600130"; AccountNumber = "12345678" }
let! payment = Api.createPaymentAsync client account.PaymentsUrl.Value (ToUkAccount bob) (Money.gbp 20m) (ValueSome "Rent") ct
let! submission = Api.submitPaymentAsync client payment.SubmissionsUrl.Value PaymentScheme.Fps ct
```

Payment creation is not idempotent: retrying after a timeout can create a second payment. Submission is: after a
timeout or 5xx, submit again; a payment already submitted answers `ApiError(410, _)`, and
`Api.listSubmissionsAsync` shows its state.

In live mode a Verify workflow asks the customer for an ID document and a selfie: send them to the link from
`Api.getIdvChecksAsync client verification.ResourcesUrl.Value` (also announced by the
`verification-resource-created` event). The sandbox completes this check itself. Griffin decides most verifications;
one it refers for review you decide with `Api.createDecisionAsync`.

## Paying a new payee safely

Check the name against the account first (Confirmation of Payee), then create the payee with that check:

```fsharp
let! check = Api.confirmPayeeAsync client org customer.Url bob AccountClassification.Personal ValueNone ct
// check.Result: CopResult.Match, CloseMatch (show check.SuggestedName to the customer) or NoMatch
let! payee = Api.createPayeeAsync client customer.PayeesUrl.Value bob (ValueSome check.Url) ct
let! payment = Api.createPaymentAsync client account.PaymentsUrl.Value (ToPayee payee.Url) (Money.gbp 50m) (ValueSome "Rent") ct
```

`UkAccount.validate` checks the 6-digit sort code and 8-digit account number; every function taking a `UkAccount`
applies it before sending. `BankAddress.toUkAccount` turns an account's listed address into one.
`Api.simulateCopAsync` returns a chosen answer (`CopSimulation.Reason CopReason.CloseMatchName`,
`CopSimulation.AccountNotFound`, ...) in the sandbox. `Api.deactivatePayeeAsync` stops further payments to a payee.

## Freezes, statements and Direct Debits

```fsharp
// A customer-requested freeze stops outbound payments (inbound still arrive) until lifted.
let! freeze =
    Api.applyRestrictionAsync client account.RestrictionsUrl.Value RestrictionType.Freeze
        RestrictionReason.CustomerRequest RestrictionLegalStatus.NotApplicable "Cool-off" ct
let! _ = Api.liftRestrictionAsync client freeze.LiftUrl.Value "Cool-off over" ct

// Statements: the period must end before today; sandbox accounts only offer XeroCsv.
let! statement = Api.requestStatementAsync client account.StatementsUrl.Value StatementFormat.GriffinPdf firstDay lastDay ct
// poll Api.getStatementAsync until Status = StatementStatus.Generated, then:
let! bytes = Api.downloadAsync client statement.DownloadUrl.Value ct

// Direct Debits the account pays (Amount is Fixed money or Variable up to a maximum), and cancelling one.
let! mandates = Api.listMandatesAsync client account.MandatesUrl.Value ct
let! _ = Api.cancelMandateAsync client mandate.Url MandateCancellationReason.CancelledByDebtor ct
```

While a freeze or block is active, creating a payment from the account answers `ApiError(422, _)` with code
`account-restricted`. `Api.downloadAsync` fetches Griffin links only (statement downloads are); a link to another
host is `ForeignLink`, for you to fetch with an `HttpClient` that carries no credentials.

`Api.resolveUrl` only follows relative links and links on `GriffinConfig.BaseUrl`'s host; anything else is
`ForeignLink`, so the API key is never sent elsewhere. Griffin returns some links with the legacy `/v0` prefix
(event payloads do); both forms work, and `Api.canonicalLink` drops the prefix for comparing and storing links.

## Claims

`Claims` builds the claims Griffin's individual workflows ask for. `Workflow.RequiredClaimTypes` lists what a
workflow needs.

```fsharp
let claims =
    [ Claims.individualIdentity "Ada" ValueNone "Example" (DateTime(1985, 6, 15))
      Claims.contactDetails "ada@example.com"
      Claims.mobileNumber "+447700900123"
      Claims.ukResidence (ValueSome "10") ValueNone "Downing Street" "London" "SW1A 2AA" "GB"
      Claims.nationality "GB"
      Claims.taxResidencies [ "GB" ]
      Claims.usCitizen false
      Claims.employment (Employed("information-technology", ValueSome "software-engineer"))
      Claims.individualIncome (Money.gbp 45000m)
      Claims.initialDeposit (Money.gbp 100m)
      Claims.purposesOfAccount [ "everyday-spending"; "savings" ]
      Claims.sourcesOfFunds [ "salary-or-bonus" ]
      Claims.cashPayments false
      Claims.internationalPaymentsCountries [] ]
```

Fields Griffin restricts to a list (industries, occupations per industry, purposes, sources of funds) take
Griffin's strings, as listed in its API reference; a wrong one comes back as an `ApiError` whose `SourcePointer`
names the field, e.g. `claims/7/occupation`. `Claims.custom` covers every other claim type, and
`Api.addClaimAsync` adds or corrects one claim after a decline.

## Pagination

List functions take the first page's URL or a page's `Next`. `Api.withQuery` adds page size, sort and filters,
and `Api.allPagesAsync` reads every page into one `Page` (all `Items`, no `Next`):

```fsharp
let first = Api.withQuery account.TransactionsUrl.Value [ "page[size]", "200" ]
let! transactions = Api.allPagesAsync (Api.listTransactionsAsync client) first ct
```

An item Griffin returns that this client cannot read lands in `Page.Unparsed` (with the reason and its JSON) instead
of failing the whole list.

Griffin allows 50 concurrent requests per organization and answers `429` beyond that; this client does not retry,
so bound your concurrency or add a retry handler to the `HttpClient`.

## Webhooks

Griffin signs each delivery with an RFC 9421 message signature (ES512 on P-521) that covers the body through
`Content-Digest`. Verify before trusting it:

```fsharp
// One cache per process (a static in a Lambda).
let keyCache = Webhook.KeyCache(maxAge = TimeSpan.FromHours 1.0, minRefreshInterval = TimeSpan.FromMinutes 5.0)

let request: Webhook.WebhookRequest =
    { Method = "POST"
      Authority = "hooks.example.com"      // the public host Griffin called
      Path = "/griffin/webhook"            // the public path Griffin called
      Query = ""
      Headers = headers                    // name, value pairs as received
      Body = bodyBytes }                   // exactly as received

match! Webhook.receiveAsync keyCache client request DateTimeOffset.UtcNow ct with
| Ok event ->
    match event.Payload with
    | TransactionCreated t -> ()
    | SubmissionUpdated s -> ()
    | DecisionCreated d -> ()
    | _ -> ()
| Error(WebhookRejected rejection) -> () // reply 400; rejection says why: Expired, DigestMismatch, UnknownKey kid, ...
| Error _ -> () // the key fetch failed with no keys held yet: reply 5xx so Griffin retries
```

Griffin may deliver an event more than once, and a captured delivery verifies again until its signature expires
(at most five minutes later), so process each `Event.Url` once.

The signature covers `@authority` and `@path`, so pass the host and path Griffin delivered to. Behind an API
Gateway custom domain or a proxy, those are the public ones, not the internal ones.

`KeyCache` fetches `/security/public-keys` again when the cached set is older than `maxAge`, or when a delivery
names a key it lacks and the last fetch is at least `minRefreshInterval` old. Griffin publishes only active keys, so
`maxAge` bounds how long a withdrawn key keeps verifying, and `minRefreshInterval` bounds how often made-up key ids
can make the cache call Griffin. When a fetch fails, the keys already held keep verifying and the fetch is tried
again on the next delivery, so an outage of Griffin's key endpoint does not reject every delivery.

This was checked against real deliveries: a sandbox webhook pointed at a temporary Lambda function URL received
Griffin's test event and eight events of a book transfer, and all of them verified and parsed. Griffin's published
keys drop leading zero bytes from their coordinates, and its `Date` header says `UTC`; both are handled, and one of
those deliveries is a regression test.

Events can arrive out of order. `Api.listEventsAsync` lists the same events, for catching up.
A payload newer than your `Griffin-Version` arrives as `Unavailable` with the version it needs.

## Request signing (live mode)

Live organizations must sign every request with Ed25519. Register the public key in the Griffin app (Settings >
Message signatures) and give the client a signer with the key id Griffin assigned:

```fsharp
let signer =
    { KeyId = "hs.…"
      Sign = fun signatureBase ct -> signWithEd25519 signatureBase ct }   // raw 64-byte signature

let client = { Config = { GriffinConfig.create apiKey with Signer = ValueSome signer }; Http = httpClient }
```

.NET has no built-in Ed25519, so `Sign` is yours: a key store, a KMS, or a library such as NSec or
BouncyCastle. Each request then carries `Date`, `Content-Digest` (sha-512), `Signature-Input` and `Signature`,
covering `@method`, `@authority`, `@path`, `@query` (when present), `content-type`, `content-length`, `date` and
`content-digest`, valid for `SignatureLifetime` (300 seconds, Griffin's maximum). Griffin requires content-type
and content-length on every signed request, so signed GET and DELETE requests send an empty body with
`Content-Type: application/json` and `Content-Length: 0`. A signer that throws gives `SigningFailed` and nothing
is sent.

This was checked against the sandbox: signed GET (with and without a query), POST, PUT, PATCH and DELETE pass
Griffin's `/security/message-signature/verify`, signed calls to real endpoints succeed, and a corrupted signature
or an unknown key id is rejected.

## From C#

The `Api` functions are ordinary static methods (`Api.getIndexAsync(client, ct)`). A few helpers cover the places
where F# types would show:

```csharp
using Griffin;
using Microsoft.FSharp.Core;

var client = new GriffinClient(GriffinConfig.Create(apiKey), httpClient);

// GetValueOrThrowAsync turns Task<Result<T, GriffinFailure>> into Task<T>; failures throw GriffinException,
// whose Failure is the GriffinFailure (ex.Failure.IsApiError, ...) and whose Message is GriffinFailure.describe.
var org = await Api.getOwnOrganizationAsync(client, ct).GetValueOrThrowAsync();

// voption parameters: default for none, FSharpValueOption<T>.Some(x) for a value. Lists: pass arrays.
Claim[] claims =
[
    Claims.individualIdentity("Ada", default, "Example", new DateTime(1985, 6, 15)),
    Claims.taxResidencies(["GB"]),
    Claims.custom("alternative-number", new Dictionary<string, object> { ["telephone-number"] = "+442012345678" }),
];
var person = await Api.createLegalPersonAsync(client, org.LegalPersonsUrl, "Ada Example", LegalPersonType.Individual, claims, ct)
    .GetValueOrThrowAsync();

// Pagination and query strings with delegates and pairs.
var first = GriffinApi.WithQuery(account.TransactionsUrl.Value, [KeyValuePair.Create("page[size]", "200")]);
var all = await GriffinApi.AllPagesAsync<Transaction>((link, token) => Api.listTransactionsAsync(client, link, token), first, ct)
    .GetValueOrThrowAsync();

// A request signer from a delegate, and a webhook delivery from ASP.NET's headers.
var signer = RequestSigner.Create("hs.…", (signatureBase, token) => SignWithEd25519Async(signatureBase, token));
var delivery = Webhook.WebhookRequest.Create("POST", publicHost, publicPath, "",
    Request.Headers.Select(h => KeyValuePair.Create(h.Key, h.Value.ToString())), bodyBytes);
```

DU values read as properties (`AccountStatus.Open`, `ex.Failure.IsInvalidRequest`) and type tests
(`ex.Failure is GriffinFailure.ApiError e`). Claim fields with hyphenated names come from a dictionary, since C#
property names cannot hold hyphens. `tests/GriffinClient.CSharpTests` compiles and runs these calls.

## Build and test

```bash
dotnet build -c Release
```

```bash
dotnet test
```

The tests run against a fake Griffin HTTP handler. Request signing is checked against RFC 9421's Ed25519 test
vector and Griffin's documented empty-body digest; webhook tests sign deliveries with a P-521 key generated during
the test. No Griffin credentials are needed.

## Licence

[The Unlicense](LICENSE): public domain.

## Examples

`examples/sandbox.fsx` runs the full flow against the Griffin sandbox: onboards a made-up individual, opens an
embedded account, moves money into it by book transfer and lists its transactions and the latest events. It
needs a sandbox key in `GRIFFIN_API_KEY` (`g-test-…`) and creates a new legal person and account on every run.
