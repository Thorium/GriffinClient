/// HTTP message signatures (RFC 9421) as Griffin uses them: Ed25519 request signatures in live mode and
/// ES512 webhook signatures, both over a signature base that covers Content-Digest (RFC 9530, sha-512).
module Griffin.MessageSignature

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

/// The Content-Digest header value for body: sha-512=:<base64>:.
let contentDigest (body: byte[]) =
    use sha = SHA512.Create()
    "sha-512=:" + Convert.ToBase64String(sha.ComputeHash body) + ":"

/// bytes left-padded with zeros to length; ValueNone when longer.
let internal leftPad (length: int) (bytes: byte[]) =
    if bytes.Length > length then
        ValueNone
    else
        ValueSome(Array.append (Array.zeroCreate (length - bytes.Length)) bytes)

/// Signature parameters, serialized in this order: created, expires, keyid, nonce, alg.
type SignatureParams =
    { /// Covered component identifiers, e.g. "@method", "content-digest".
      Components: string list
      Created: int64 voption
      Expires: int64 voption
      KeyId: string voption
      Nonce: string voption
      Alg: string voption }

let private quote (s: string) =
    "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

let private isWrapped (opening: char) (closing: char) (s: string) =
    s.Length >= 2 && s[0] = opening && s[s.Length - 1] = closing

let private unquote (s: string) =
    if isWrapped '"' '"' s then
        s.Substring(1, s.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\")
    else
        s

/// The inner list with parameters, as it follows "label=" in Signature-Input and "@signature-params" in the base.
let serializeParams (p: SignatureParams) =
    let parameters =
        [ "created", p.Created |> ValueOption.map string
          "expires", p.Expires |> ValueOption.map string
          "keyid", p.KeyId |> ValueOption.map quote
          "nonce", p.Nonce |> ValueOption.map quote
          "alg", p.Alg |> ValueOption.map quote ]
        |> List.choose (fun (name, value) ->
            match value with
            | ValueSome v -> Some $";{name}={v}"
            | ValueNone -> None)

    "(" + String.Join(" ", p.Components |> List.map quote) + ")" + String.concat "" parameters

/// One line per covered component, then "@signature-params" with serializedParams exactly as sent.
let private baseText (lines: (string * string) list) (serializedParams: string) =
    let componentLines = lines |> List.map (fun (c, v) -> $"\"{c}\": {v.Trim()}")
    String.Join("\n", componentLines @ [ $"\"@signature-params\": {serializedParams}" ])

/// The signature base over components, their values from componentValue. Error names the first covered
/// component the message lacks.
let signatureBase
    (componentValue: string -> string voption)
    (components: string list)
    (serializedParams: string)
    : Result<string, string> =
    let values = components |> List.map (fun c -> c, componentValue c)

    match values |> List.tryFind (snd >> ValueOption.isNone) with
    | Some(missing, _) -> Error missing
    | None -> Ok(baseText (values |> List.map (fun (c, v) -> c, ValueOption.get v)) serializedParams)

/// The members of a structured-field dictionary or list: header split at commas outside quoted strings and
/// inner lists.
let private splitTopLevel (header: string) =
    let rec go i inQuote escaped depth start members =
        let cut () = header.Substring(start, i - start) :: members

        if i = header.Length then
            List.rev (cut ())
        else
            match header[i], inQuote with
            | _, true when escaped -> go (i + 1) true false depth start members
            | '\\', true -> go (i + 1) true true depth start members
            | '"', true -> go (i + 1) false false depth start members
            | _, true -> go (i + 1) true false depth start members
            | '"', false -> go (i + 1) true false depth start members
            | '(', false -> go (i + 1) false false (depth + 1) start members
            | ')', false -> go (i + 1) false false (depth - 1) start members
            | ',', false when depth = 0 -> go (i + 1) false false depth (i + 1) (cut ())
            | _ -> go (i + 1) false false depth start members

    go 0 false false 0 0 []

/// Splits a structured-field dictionary (Signature-Input, Signature, Content-Digest) into label -> member text,
/// keeping quoted strings and inner lists intact. A repeated label keeps its last member.
let parseDictionary (header: string) : Map<string, string> =
    splitTopLevel header
    |> List.map _.Trim()
    |> List.choose (fun m ->
        match m.IndexOf '=' with
        | i when i > 0 -> Some(m.Substring(0, i).Trim(), m.Substring(i + 1).Trim())
        | _ -> None)
    |> Map.ofList

/// Reads a Signature-Input member: the covered components and the parameters this client uses.
let parseParams (memberText: string) : Result<SignatureParams, string> =
    let close = memberText.IndexOf ')'

    if memberText.Length = 0 || memberText[0] <> '(' || close < 0 then
        Error "Signature-Input member is not an inner list"
    else
        let components =
            memberText.Substring(1, close - 1).Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map unquote
            |> List.ofArray

        let parameters =
            memberText.Substring(close + 1).Split([| ';' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.choose (fun p ->
                match p.IndexOf '=' with
                | i when i > 0 -> Some(p.Substring(0, i).Trim(), unquote (p.Substring(i + 1).Trim()))
                | _ -> None)
            |> Map.ofArray

        let text name =
            match parameters.TryFind name with
            | Some v -> ValueSome v
            | None -> ValueNone

        let number name =
            text name
            |> ValueOption.bind (fun v ->
                match Int64.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                | true, n -> ValueSome n
                | _ -> ValueNone)

        Ok
            { Components = components
              Created = number "created"
              Expires = number "expires"
              KeyId = text "keyid"
              Nonce = text "nonce"
              Alg = text "alg" }

/// The bytes of a byte-sequence member, :<base64>:.
let parseByteSequence (memberText: string) : Result<byte[], string> =
    let t = memberText.Trim()

    if isWrapped ':' ':' t then
        try
            Ok(Convert.FromBase64String(t.Substring(1, t.Length - 2)))
        with :? FormatException ->
            Error "Signature is not base64"
    else
        Error "Signature member is not a byte sequence"

/// What a request signature covers.
type SigningInput =
    { /// e.g. "POST".
      Method: string
      /// Host, plus the port when it is not the scheme's default.
      Authority: string
      /// Absolute path as sent, e.g. "/organizations/og.1/legal-persons".
      Path: string
      /// The query with its leading '?', or "" for none.
      Query: string
      /// Body bytes as sent; empty for a request without a body.
      Body: byte[]
      /// The Content-Type header as sent.
      ContentType: string }

/// Label of the signatures this client adds.
[<Literal>]
let Label = "griffin-client"

/// The headers that sign a request: Date, Content-Digest, Signature-Input, Signature. It covers @method,
/// @authority, @path, @query (when there is one), content-type, content-length, date and content-digest.
/// Griffin requires content-type and content-length on every signed request, so a request without a body
/// must still send Content-Type and Content-Length: 0, and carries the digest of the empty string.
let signAsync
    (signer: RequestSigner)
    (input: SigningInput)
    (created: DateTimeOffset)
    (lifetime: TimeSpan)
    (nonce: string)
    (ct: CancellationToken)
    : Task<(string * string) list> =
    task {
        let date = created.UtcDateTime.ToString("r", CultureInfo.InvariantCulture)
        let digest = contentDigest input.Body

        let lines =
            [ yield "@method", input.Method.ToUpperInvariant()
              yield "@authority", input.Authority.ToLowerInvariant()
              yield "@path", input.Path
              if input.Query <> "" then
                  yield "@query", input.Query
              yield "content-type", input.ContentType
              yield "content-length", string input.Body.Length
              yield "date", date
              yield "content-digest", digest ]

        let serialized =
            serializeParams
                { Components = lines |> List.map fst
                  Created = ValueSome(created.ToUnixTimeSeconds())
                  Expires = ValueSome(created.Add(lifetime).ToUnixTimeSeconds())
                  KeyId = ValueSome signer.KeyId
                  Nonce = ValueSome nonce
                  Alg = ValueSome "ed25519" }

        let! signature = signer.Sign (Encoding.UTF8.GetBytes(baseText lines serialized)) ct

        return
            [ "Date", date
              "Content-Digest", digest
              "Signature-Input", $"{Label}={serialized}"
              "Signature", $"{Label}=:{Convert.ToBase64String signature}:" ]
    }

/// An ASN.1 DER ECDSA signature (SEQUENCE of INTEGER r, INTEGER s) as IEEE P1363 r||s, each fieldBytes long.
let derToP1363 (fieldBytes: int) (der: byte[]) : byte[] voption =
    let readLength i =
        if i >= der.Length then
            ValueNone
        elif der[i] < 0x80uy then
            ValueSome(int der[i], i + 1)
        else
            let count = int (der[i] &&& 0x7Fuy)

            if count < 1 || count > 2 || i + count >= der.Length then
                ValueNone
            else
                let length = der[i + 1 .. i + count] |> Array.fold (fun n b -> (n <<< 8) ||| int b) 0
                ValueSome(length, i + 1 + count)

    let readInteger i =
        if i >= der.Length || der[i] <> 0x02uy then
            ValueNone
        else
            readLength (i + 1)
            |> ValueOption.bind (fun (length, start) ->
                if length = 0 || start + length > der.Length then
                    ValueNone
                else
                    der[start .. start + length - 1]
                    |> Array.skipWhile ((=) 0uy)
                    |> leftPad fieldBytes
                    |> ValueOption.map (fun padded -> padded, start + length))

    if der.Length < 8 || der[0] <> 0x30uy then
        ValueNone
    else
        readLength 1
        |> ValueOption.filter (fun (sequenceLength, start) -> start + sequenceLength = der.Length)
        |> ValueOption.bind (fun (_, start) -> readInteger start)
        |> ValueOption.bind (fun (r, next) ->
            readInteger next
            |> ValueOption.filter (fun (_, finish) -> finish = der.Length)
            |> ValueOption.map (fun (s, _) -> Array.append r s))
