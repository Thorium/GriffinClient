/// Webhook delivery verification. Griffin signs each delivery with an RFC 9421 message signature (ES512 on
/// P-521) that covers the body through Content-Digest; its public keys are at /security/public-keys.
/// A verified delivery can still be replayed until its signature expires (at most five minutes), and Griffin
/// may deliver an event more than once: process each Event.Url once.
module Griffin.Webhook

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Griffin.ResultBuilder

/// A delivery as your endpoint received it. Method, Authority and Path must be what Griffin sent to: behind a
/// proxy or an API Gateway custom domain, the public host and path, not the internal ones.
type WebhookRequest =
    { Method: string
      /// Public host, plus the port when it is not the default.
      Authority: string
      Path: string
      /// The query with its leading '?', or "" for none.
      Query: string
      /// Header values by name; names match case-insensitively.
      Headers: (string * string) list
      /// The body bytes exactly as received.
      Body: byte[] }

    /// For C#: a delivery from header name/value pairs, e.g. from ASP.NET's
    /// Request.Headers.Select(h => KeyValuePair.Create(h.Key, h.Value.ToString())). query is "" or "?...".
    static member Create
        (
            method: string,
            authority: string,
            path: string,
            query: string,
            headers: Collections.Generic.IEnumerable<Collections.Generic.KeyValuePair<string, string>>,
            body: byte[]
        ) =
        { Method = method
          Authority = authority
          Path = path
          Query = query
          Headers = headers |> Seq.map (fun h -> h.Key, h.Value) |> List.ofSeq
          Body = body }

let private headerOf (request: WebhookRequest) (name: string) =
    match
        request.Headers
        |> List.filter (fun (n, _) -> String.Equals(n, name, StringComparison.OrdinalIgnoreCase))
        |> List.map (snd >> _.Trim())
    with
    | [] -> ValueNone
    | values -> ValueSome(String.Join(", ", values))

let private requiredHeader request name =
    match headerOf request name with
    | ValueSome value -> Ok value
    | ValueNone -> Error(MissingHeader name)

let private base64UrlDecode (s: string) =
    let standard = s.Replace('-', '+').Replace('_', '/')
    Convert.FromBase64String(standard + String('=', (4 - standard.Length % 4) % 4))

let private fixedTimeEquals (a: byte[]) (b: byte[]) =
    a.Length = b.Length && Array.fold2 (fun acc (x: byte) (y: byte) -> acc ||| (x ^^^ y)) 0uy a b = 0uy

/// P-521 coordinates and r, s are 66 bytes.
[<Literal>]
let private P521Bytes = 66

let private sha512 (bytes: byte[]) =
    use sha = SHA512.Create()
    sha.ComputeHash bytes

let private bodyDigestMatches (request: WebhookRequest) =
    result {
        let! header = requiredHeader request "content-digest"

        let! digest =
            match MessageSignature.parseDictionary header |> Map.tryFind "sha-512" with
            | Some digest -> Ok digest
            | None -> Error(MalformedSignature "Content-Digest has no sha-512 digest")

        let! expected = MessageSignature.parseByteSequence digest |> Result.mapError MalformedSignature
        return! if fixedTimeEquals expected (sha512 request.Body) then Ok() else Error DigestMismatch
    }

/// A key coordinate as 66 bytes. Griffin's JWKs drop leading zero bytes, so it is padded back.
let private coordinate (key: WebhookKey) (encoded: string) =
    let decoded =
        try
            ValueSome(base64UrlDecode encoded)
        with :? FormatException ->
            ValueNone

    match decoded |> ValueOption.bind (MessageSignature.leftPad P521Bytes) with
    | ValueSome padded -> Ok padded
    | ValueNone -> Error(InvalidKey(key.KeyId, "coordinates are not P-521 base64url"))

let private verifySignature (key: WebhookKey) (signature: byte[]) (signatureBase: string) =
    result {
        do!
            if key.Algorithm = "ES512" && key.Curve = "P-521" then
                Ok()
            else
                Error(UnsupportedKey(key.KeyId, key.Algorithm, key.Curve))

        let! rs =
            if signature.Length = 2 * P521Bytes then
                Ok signature
            else
                match MessageSignature.derToP1363 P521Bytes signature with
                | ValueSome rs -> Ok rs
                | ValueNone -> Error(MalformedSignature "Signature is not an ES512 signature")

        let! x = coordinate key key.X
        let! y = coordinate key key.Y

        let! matches =
            try
                use ecdsa = ECDsa.Create(ECParameters(Curve = ECCurve.NamedCurves.nistP521, Q = ECPoint(X = x, Y = y)))
                Ok(ecdsa.VerifyData(Encoding.UTF8.GetBytes signatureBase, rs, HashAlgorithmName.SHA512))
            with
            // Windows reports a point off the curve as PlatformNotSupportedException.
            | :? CryptographicException
            | :? PlatformNotSupportedException as e -> Error(InvalidKey(key.KeyId, e.Message))

        return! if matches then Ok() else Error SignatureMismatch
    }

let private componentValue (request: WebhookRequest) =
    function
    | "@method" -> ValueSome(request.Method.ToUpperInvariant())
    | "@authority" -> ValueSome(request.Authority.ToLowerInvariant())
    | "@path" -> ValueSome request.Path
    | "@query" -> ValueSome(if request.Query = "" then "?" else request.Query)
    | name when name.StartsWith("@", StringComparison.Ordinal) -> ValueNone
    | name -> headerOf request name

let private verifyOne (keys: WebhookKey list) (request: WebhookRequest) (now: DateTimeOffset) (memberText: string) (signatureText: string) =
    result {
        let! parameters = MessageSignature.parseParams memberText |> Result.mapError MalformedSignature
        let! signature = MessageSignature.parseByteSequence signatureText |> Result.mapError MalformedSignature

        let! key =
            match parameters.KeyId with
            | ValueNone -> Error(MalformedSignature "Signature-Input has no keyid")
            | ValueSome kid ->
                match keys |> List.tryFind (fun k -> k.KeyId = kid) with
                | Some key -> Ok key
                | None -> Error(UnknownKey kid)

        do! if parameters.Components |> List.contains "content-digest" then Ok() else Error DigestNotCovered

        do!
            match parameters.Expires with
            | ValueNone -> Error NoExpiry
            | ValueSome expires when now.ToUnixTimeSeconds() > expires -> Error(Expired(DateTimeOffset.FromUnixTimeSeconds expires))
            | ValueSome _ -> Ok()

        let! signatureBase =
            MessageSignature.signatureBase (componentValue request) parameters.Components memberText
            |> Result.mapError MissingComponent

        do! verifySignature key signature signatureBase
        return! bodyDigestMatches request
    }

/// Key ids the delivery's Signature-Input names.
let keyIdsOf (request: WebhookRequest) =
    match headerOf request "signature-input" with
    | ValueNone -> []
    | ValueSome header ->
        MessageSignature.parseDictionary header
        |> Map.toList
        |> List.choose (fun (_, m) ->
            match MessageSignature.parseParams m with
            | Ok { KeyId = ValueSome kid } -> Some kid
            | _ -> None)

/// Verifies a delivery against keys: a signature by one of them over the components its Signature-Input lists,
/// including content-digest, not expired at now, and a body matching Content-Digest. With several signatures,
/// one valid signature is enough; otherwise the last one's rejection is returned.
let verifyWithKeys (keys: WebhookKey list) (request: WebhookRequest) (now: DateTimeOffset) : Result<unit, WebhookRejection> =
    result {
        let! inputHeader = requiredHeader request "signature-input"
        let! signatureHeader = requiredHeader request "signature"
        let signatures = MessageSignature.parseDictionary signatureHeader

        let attempts =
            MessageSignature.parseDictionary inputHeader
            |> Map.toList
            |> List.map (fun (label, memberText) ->
                match signatures.TryFind label with
                | Some signatureText -> verifyOne keys request now memberText signatureText
                | None -> Error(MalformedSignature $"No Signature for label {label}"))

        return!
            match attempts |> List.tryFind Result.isOk, List.tryLast attempts with
            | Some ok, _ -> ok
            | None, Some rejection -> rejection
            | None, None -> Error NoSignatures
    }

/// Griffin's webhook keys, fetched when the cached set is older than maxAge, or when a delivery names a key the
/// set lacks and the last fetch is at least minRefreshInterval old. Griffin publishes only active keys, so
/// maxAge bounds how long a key it has withdrawn keeps verifying; minRefreshInterval bounds how often unknown
/// key ids can make the cache call Griffin. When a fetch fails, the keys already held are used (and the fetch
/// is tried again on the next call), so an outage of Griffin's key endpoint does not reject every delivery.
/// Safe to share between concurrent requests, which may each fetch at the same moment; keep one per process.
type KeyCache(maxAge: TimeSpan, minRefreshInterval: TimeSpan) =
    [<VolatileField>]
    let mutable snapshot: struct (WebhookKey list * DateTimeOffset) voption = ValueNone

    member _.MaxAge = maxAge
    member _.MinRefreshInterval = minRefreshInterval

    /// Keys covering keyIds where Griffin has them, fetching as described above.
    member _.GetAsync
        (client: GriffinClient, keyIds: string list, now: DateTimeOffset, ct: CancellationToken)
        : Task<Result<WebhookKey list, GriffinFailure>> =
        let held = snapshot

        let needsFetch =
            match held with
            | ValueNone -> true
            | ValueSome(struct (keys, fetchedAt)) ->
                let age = now - fetchedAt
                let missing = keyIds |> List.exists (fun kid -> keys |> List.forall (fun k -> k.KeyId <> kid))
                age >= maxAge || (missing && age >= minRefreshInterval)

        match held with
        | ValueSome(struct (keys, _)) when not needsFetch -> Task.FromResult(Ok keys)
        | _ ->
            task {
                let! fetched = Api.getWebhookKeysAsync client ct

                return
                    match fetched, held with
                    | Ok keys, _ ->
                        snapshot <- ValueSome(struct (keys, now))
                        Ok keys
                    | Error _, ValueSome(struct (keys, _)) -> Ok keys
                    | Error e, ValueNone -> Error e
            }

/// Verifies a delivery with keys from cache.
let verifyAsync (cache: KeyCache) (client: GriffinClient) (request: WebhookRequest) (now: DateTimeOffset) ct =
    task {
        match! cache.GetAsync(client, keyIdsOf request, now, ct) with
        | Error e -> return Error e
        | Ok keys -> return verifyWithKeys keys request now |> Result.mapError WebhookRejected
    }

/// Verifies a delivery, then parses its body as an Event. Process each Event.Url once: deliveries can repeat.
let receiveAsync (cache: KeyCache) (client: GriffinClient) (request: WebhookRequest) (now: DateTimeOffset) ct =
    task {
        match! verifyAsync cache client request now ct with
        | Error e -> return Error e
        | Ok() -> return Event.parse (Encoding.UTF8.GetString request.Body)
    }
