module WebhookTests

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open Griffin
open FakeGriffin

let private ct = CancellationToken.None

let private base64Url (bytes: byte[]) =
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

let private now = DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero)

/// A P-521 key pair standing in for Griffin's.
let private griffinKey = ECDsa.Create(ECCurve.NamedCurves.nistP521)

[<Literal>]
let private kid = "griffin-2026-09-28-1"

let private jwk keyId =
    let p = griffinKey.ExportParameters false

    { KeyId = keyId
      Algorithm = "ES512"
      Curve = "P-521"
      X = base64Url p.Q.X
      Y = base64Url p.Q.Y }

let private jwksJson (keyIds: string list) =
    let keys =
        keyIds
        |> List.map (fun k ->
            let key = jwk k
            $"""{{"alg":"ES512","crv":"P-521","kid":"{k}","kty":"EC","use":"sig","x":"{key.X}","y":"{key.Y}"}}""")

    $"""{{"keys":[{String.Join(",", keys)}],"links":{{"prev":null,"next":null}}}}"""

let private body =
    Encoding.UTF8.GetBytes(
        """{"event-url":"/events/ev.1","event-type":"account-status-updated","event-payload":{"account-url":"/v0/bank/accounts/ba.1","account-status":"open"},"created-at":"2026-10-26T11:59:00Z"}"""
    )

type private Delivery =
    { Created: DateTimeOffset
      Components: string list
      SignedPath: string
      SignedBody: byte[]
      Der: bool }

let private delivery =
    { Created = now.AddSeconds -10.0
      Components = [ "date"; "@method"; "@path"; "@authority"; "content-type"; "content-length"; "content-digest" ]
      SignedPath = "/webhooks/griffin"
      SignedBody = body
      Der = true }

/// Signs a delivery the way Griffin's guide shows: label = key id, created/expires 300 s apart.
let private signed (d: Delivery) : Webhook.WebhookRequest =
    use sha = SHA512.Create()
    let digest = "sha-512=:" + Convert.ToBase64String(sha.ComputeHash d.SignedBody) + ":"
    let date = d.Created.UtcDateTime.ToString "r"

    let value =
        function
        | "date" -> date
        | "@method" -> "POST"
        | "@path" -> d.SignedPath
        | "@authority" -> "hooks.example.com"
        | "content-type" -> "application/json"
        | "content-length" -> string d.SignedBody.Length
        | "content-digest" -> digest
        | other -> failwith other

    let inner = "(" + String.Join(" ", d.Components |> List.map (fun c -> $"\"{c}\"")) + ")"

    let parameters =
        $"{inner};created={d.Created.ToUnixTimeSeconds()};expires={d.Created.AddSeconds(300.0).ToUnixTimeSeconds()};nonce=\"n-1\";keyid=\"{kid}\""

    let signatureBase =
        String.Join("\n", (d.Components |> List.map (fun c -> $"\"{c}\": {value c}")) @ [ "\"@signature-params\": " + parameters ])

    let format =
        if d.Der then DSASignatureFormat.Rfc3279DerSequence else DSASignatureFormat.IeeeP1363FixedFieldConcatenation

    let signature = griffinKey.SignData(Encoding.UTF8.GetBytes signatureBase, HashAlgorithmName.SHA512, format)

    { Method = "POST"
      Authority = "hooks.example.com"
      Path = "/webhooks/griffin"
      Query = ""
      Headers =
        [ "Date", date
          "Content-Type", "application/json"
          "Content-Length", string body.Length
          "Content-Digest", digest
          "Signature-Input", $"{kid}={parameters}"
          "Signature", $"{kid}=:{Convert.ToBase64String signature}:" ]
      Body = body }

let private keys = [ jwk kid ]

[<Fact>]
let ``a Griffin-style delivery verifies`` () =
    Assert.Equal(Ok(), Webhook.verifyWithKeys keys (signed delivery) now)

[<Fact>]
let ``a raw r||s signature verifies too`` () =
    Assert.Equal(Ok(), Webhook.verifyWithKeys keys (signed { delivery with Der = false }) now)

[<Fact>]
let ``header names match case-insensitively`` () =
    let request = signed delivery
    let lower = { request with Headers = request.Headers |> List.map (fun (n, v) -> n.ToLowerInvariant(), v) }
    Assert.Equal(Ok(), Webhook.verifyWithKeys keys lower now)

[<Fact>]
let ``a body changed after signing fails the digest`` () =
    let request = signed delivery
    let tampered = { request with Body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace("open", "closed")) }

    Assert.Equal(Error DigestMismatch, Webhook.verifyWithKeys keys tampered now)

[<Fact>]
let ``a body and digest both replaced fail the signature`` () =
    let request = signed { delivery with SignedBody = body }
    let otherBody = "{}"B
    use sha = SHA512.Create()
    let otherDigest = "sha-512=:" + Convert.ToBase64String(sha.ComputeHash otherBody) + ":"

    let replaced =
        { request with
            Body = otherBody
            Headers =
                request.Headers
                |> List.map (fun (n, v) -> if n = "Content-Digest" then n, otherDigest else n, v) }

    Assert.Equal(Error SignatureMismatch, Webhook.verifyWithKeys keys replaced now)

[<Fact>]
let ``an expired signature is rejected with its expiry`` () =
    let expiredAt = DateTimeOffset.FromUnixTimeSeconds(delivery.Created.AddSeconds(300.0).ToUnixTimeSeconds())
    Assert.Equal(Error(Expired expiredAt), Webhook.verifyWithKeys keys (signed delivery) (now.AddMinutes 6.0))

[<Fact>]
let ``an unknown key is rejected with its key id`` () =
    Assert.Equal(Error(UnknownKey kid), Webhook.verifyWithKeys [ jwk "other-kid" ] (signed delivery) now)

[<Fact>]
let ``a signature that does not cover content-digest is rejected`` () =
    let request = signed { delivery with Components = [ "date"; "@method"; "@path"; "@authority" ] }
    Assert.Equal(Error DigestNotCovered, Webhook.verifyWithKeys keys request now)

[<Fact>]
let ``the path must be the one Griffin delivered to`` () =
    let request = signed { delivery with SignedPath = "/public/webhooks/griffin" }
    Assert.Equal(Error SignatureMismatch, Webhook.verifyWithKeys keys request now)

[<Fact>]
let ``a covered header the delivery lacks is MissingComponent`` () =
    let request = signed delivery
    let withoutDate = { request with Headers = request.Headers |> List.filter (fun (n, _) -> n <> "Date") }
    Assert.Equal(Error(MissingComponent "date"), Webhook.verifyWithKeys keys withoutDate now)

[<Fact>]
let ``missing signature headers are rejected`` () =
    let request = signed delivery
    let without name = { request with Headers = request.Headers |> List.filter (fun (n, _) -> n <> name) }
    Assert.Equal(Error(MissingHeader "signature"), Webhook.verifyWithKeys keys (without "Signature") now)
    Assert.Equal(Error(MissingHeader "signature-input"), Webhook.verifyWithKeys keys (without "Signature-Input") now)
    Assert.Equal(Error(MissingComponent "content-digest"), Webhook.verifyWithKeys keys (without "Content-Digest") now)

[<Fact>]
let ``receive verifies with cached keys and parses the event`` () : Task =
    task {
        use handler =
            new FakeGriffinHandler(fun _ target _ ->
                Assert.Equal("/security/public-keys", target)
                ok (jwksJson [ kid ]))

        let client = clientWith handler
        let cache = Webhook.KeyCache(TimeSpan.FromHours 1.0, TimeSpan.FromMinutes 5.0)

        for _ in 1..3 do
            match! Webhook.receiveAsync cache client (signed delivery) now ct with
            | Ok e -> Assert.Equal(AccountStatusUpdated("/v0/bank/accounts/ba.1", AccountStatus.Open), e.Payload)
            | Error e -> failwith $"%A{e}"

        Assert.Single handler.Calls |> ignore
    }

[<Fact>]
let ``an unknown key id refreshes the cache, but at most once per minRefreshInterval`` () : Task =
    task {
        let published = ref [ "old-kid" ]
        use handler = new FakeGriffinHandler(fun _ _ _ -> ok (jwksJson published.Value))
        let client = clientWith handler
        let cache = Webhook.KeyCache(TimeSpan.FromHours 1.0, TimeSpan.FromMinutes 5.0)

        let! _ = cache.GetAsync(client, [ "old-kid" ], now, ct)
        Assert.Equal(1, handler.Calls.Length)

        // Griffin rotates; a delivery names the new key one minute later: too soon to refresh.
        published.Value <- [ kid ]
        let! early = Webhook.verifyAsync cache client (signed delivery) (now.AddMinutes 1.0) ct
        Assert.True(Result.isError early)
        Assert.Equal(1, handler.Calls.Length)

        // Past minRefreshInterval the miss refreshes and the delivery verifies.
        let later = now.AddMinutes 6.0
        let request = signed { delivery with Created = later.AddSeconds -10.0 }
        let! verified = Webhook.verifyAsync cache client request later ct
        Assert.Equal(Ok(), verified)
        Assert.Equal(2, handler.Calls.Length)
    }

[<Fact>]
let ``keys older than maxAge are fetched again, and failed fetches are not cached`` () : Task =
    task {
        let fail = ref true

        use handler =
            new FakeGriffinHandler(fun _ _ _ ->
                if fail.Value then
                    fail.Value <- false
                    griffinError 500 "Internal" ""
                else
                    ok (jwksJson [ kid ]))

        let client = clientWith handler
        let cache = Webhook.KeyCache(TimeSpan.FromMinutes 30.0, TimeSpan.FromMinutes 5.0)

        let! first = cache.GetAsync(client, [ kid ], now, ct)
        Assert.True(Result.isError first)
        let! second = cache.GetAsync(client, [ kid ], now, ct)
        Assert.True(Result.isOk second)
        let! _ = cache.GetAsync(client, [ kid ], now.AddMinutes 29.0, ct)
        Assert.Equal(2, handler.Calls.Length)
        let! _ = cache.GetAsync(client, [ kid ], now.AddMinutes 31.0, ct)
        Assert.Equal(3, handler.Calls.Length)
    }

[<Fact>]
let ``when a refresh fails the keys already held keep verifying`` () : Task =
    task {
        let up = ref true

        use handler =
            new FakeGriffinHandler(fun _ _ _ -> if up.Value then ok (jwksJson [ kid ]) else griffinError 503 "Unavailable" "")

        let client = clientWith handler
        let cache = Webhook.KeyCache(TimeSpan.FromMinutes 30.0, TimeSpan.FromMinutes 5.0)
        let! _ = cache.GetAsync(client, [ kid ], now, ct)

        // Griffin's key endpoint goes down; the held set is past maxAge, so the cache tries and falls back.
        up.Value <- false
        let later = now.AddMinutes 40.0
        let request = signed { delivery with Created = later.AddSeconds -10.0 }
        let! verified = Webhook.verifyAsync cache client request later ct
        Assert.Equal(Ok(), verified)
        Assert.Equal(2, handler.Calls.Length)

        // It keeps trying on later calls rather than trusting the stale set as fresh.
        let! _ = Webhook.verifyAsync cache client request later ct
        Assert.Equal(3, handler.Calls.Length)
    }

/// Griffin's published key griffin-2026-09-28-1: its y has a leading zero byte dropped (64 of 66 bytes).
let private griffinPublishedKey =
    { KeyId = "griffin-2026-09-28-1"
      Algorithm = "ES512"
      Curve = "P-521"
      X = "AWQOG2paiMLBtCdo-8d1pwxsLaD6mKow7Zh8RDtMhSKymRT6tSDVYON5ubo9rVmJv2_3Zr3-W_Y1-vAhovATczSA"
      Y = "TZNUzXaUSiRGRS8g8usd3T4jaop3rLQAiX4SqNw-oGsUd8M6j1OmSwDtpHAcEg_XOLmqvqLq6hIq_Dna4REEV9Y" }

/// A test event Griffin delivered to a temporary Lambda function URL in the sandbox, 2026-10-27.
let private griffinDelivery: Webhook.WebhookRequest =
    { Method = "POST"
      Authority = "u5yhdgnzubhqhavfq3e5l2lqlq0issln.lambda-url.eu-west-2.on.aws"
      Path = "/webhooks/griffin"
      Query = ""
      Headers =
        [ "date", "Tue, 27 Oct 2026 11:26:00 UTC"
          "content-type", "application/json; charset=utf-8"
          "content-length", "137"
          "content-digest", "sha-512=:YOvieMd5lFGBiNz9pHScaNTppyYSnmIQscwI9/zOwCNHMI+t0tNA3N/wqe9JdAWWCyIBVGkd7/59u+d/Zy7Qjw==:"
          "signature-input",
          "griffin-2026-09-28-1=(\"date\" \"@method\" \"@path\" \"@authority\" \"content-type\" \"content-length\" \"content-digest\");created=1793100360;expires=1793100660;nonce=\"3cd5818e-59a6-4a6d-8e9a-cc8e15a9defe\";keyid=\"griffin-2026-09-28-1\""
          "signature",
          "griffin-2026-09-28-1=:MIGIAkIA8b3fZ1dMNj518mXugogqwhx0e68ttqirAQZozhq/FkfXDXJj6YMm8azSu4DhIJtIGwOwUNfkjy6TGfrwVvQ7vwICQgGscTWg0r8iTNklZGjuVsaIxhjHgF+XpZ1Jyo9W1Apl9PytAvSSTVC962dqhEUYA1jM5vQ5O/zFXZNgBmGpcb17gg==:" ]
      Body =
        Encoding.UTF8.GetBytes
            """{"event-url":"/v0/events/ev.GjYRIRqnUxCLhcNnPMsQ3g","event-type":"test-event","event-payload":{},"created-at":"2026-10-27T11:26:00.724Z"}""" }

let private griffinDeliveredAt = DateTimeOffset.FromUnixTimeMilliseconds 1793100360792L

[<Fact>]
let ``a real Griffin delivery verifies against Griffin's published key`` () =
    Assert.Equal(Ok(), Webhook.verifyWithKeys [ griffinPublishedKey ] griffinDelivery griffinDeliveredAt)

[<Fact>]
let ``a real Griffin delivery fails once its body, path or time is wrong`` () =
    let verify request at = Webhook.verifyWithKeys [ griffinPublishedKey ] request at
    let changedBody = { griffinDelivery with Body = "{}"B }
    Assert.True(Result.isError (verify changedBody griffinDeliveredAt))
    Assert.Equal(Error SignatureMismatch, verify { griffinDelivery with Path = "/internal" } griffinDeliveredAt)

    match verify griffinDelivery (griffinDeliveredAt.AddMinutes 10.0) with
    | Error(Expired _) -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``JWK coordinates without their leading zero bytes still verify`` () =
    // Find a key whose X and Y both start with 0x00, as Griffin then publishes them shortened.
    let rec keyWithZeroLeads () =
        let k = ECDsa.Create(ECCurve.NamedCurves.nistP521)
        let q = (k.ExportParameters false).Q
        if q.X[0] = 0uy && q.Y[0] = 0uy then k, q else (k.Dispose(); keyWithZeroLeads ())

    let key, q = keyWithZeroLeads ()
    use key = key
    let trim (bytes: byte[]) = bytes |> Array.skipWhile ((=) 0uy)
    let jwk = { KeyId = "trimmed"; Algorithm = "ES512"; Curve = "P-521"; X = base64Url (trim q.X); Y = base64Url (trim q.Y) }
    Assert.True(jwk.X.Length < 88 && jwk.Y.Length < 88)

    let digest = MessageSignature.contentDigest Array.empty
    let inner = "(\"@method\" \"content-digest\");expires=9999999999;keyid=\"trimmed\""
    let fullBase = $"\"@method\": POST\n\"content-digest\": {digest}\n\"@signature-params\": {inner}"
    let fullSignature = key.SignData(Encoding.UTF8.GetBytes fullBase, HashAlgorithmName.SHA512)

    let request: Webhook.WebhookRequest =
        { Method = "POST"
          Authority = "x"
          Path = "/"
          Query = ""
          Headers =
            [ "content-digest", digest
              "signature-input", $"s={inner}"
              "signature", $"s=:{Convert.ToBase64String fullSignature}:" ]
          Body = Array.empty }

    Assert.Equal(Ok(), Webhook.verifyWithKeys [ jwk ] request now)

[<Fact>]
let ``a malformed key is a rejection, not an exception`` () =
    let broken = { griffinPublishedKey with Y = "AAAA" }

    let tooLong = { griffinPublishedKey with X = base64Url (Array.create 67 1uy) }
    Assert.True(Result.isError (Webhook.verifyWithKeys [ broken ] griffinDelivery griffinDeliveredAt))

    match Webhook.verifyWithKeys [ tooLong ] griffinDelivery griffinDeliveredAt with
    | Error(InvalidKey(keyId, _)) -> Assert.Equal("griffin-2026-09-28-1", keyId)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``the published key set parses`` () : Task =
    task {
        // Shape of GET /security/public-keys in the sandbox, 2026-10-26.
        let json =
            """{"keys":[{"alg":"ES512","crv":"P-521","kid":"griffin-2026-09-28-1","kty":"EC","use":"sig","x":"AWQOG2paiMLBtCdo-8d1pwxsLaD6mKow7Zh8RDtMhSKymRT6tSDVYON5ubo9rVmJv2_3Zr3-W_Y1-vAhovATczSA","y":"TZNUzXaUSiRGRS8g8usd3T4jaop3rLQAiX4SqNw-oGsUd8M6j1OmSwDtpHAcEg_XOLmqvqLq6hIq_Dna4REEV9Y"}],"links":{"prev":null,"next":null}}"""

        use handler = new FakeGriffinHandler(fun _ _ _ -> ok json)
        match! Api.getWebhookKeysAsync (clientWith handler) ct with
        | Ok [ k ] ->
            Assert.Equal("griffin-2026-09-28-1", k.KeyId)
            Assert.Equal("ES512", k.Algorithm)
            Assert.Equal("P-521", k.Curve)
        | other -> failwith $"%A{other}"
    }
