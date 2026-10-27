module SigningTests

open System
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Org.BouncyCastle.Crypto.Parameters
open Org.BouncyCastle.Crypto.Signers
open Xunit
open Griffin
open FakeGriffin

let private ct = CancellationToken.None

let private base64UrlDecode (s: string) =
    let standard = s.Replace('-', '+').Replace('_', '/')
    Convert.FromBase64String(standard + String('=', (4 - standard.Length % 4) % 4))

/// test-key-ed25519 of RFC 9421 appendix B.1.4.
let private rfcPrivateKey =
    Ed25519PrivateKeyParameters(base64UrlDecode "n4Ni-HpISpVObnQMW0wOhCKROaIKqKtW_2ZYb2p9KcU", 0)

let private rfcPublicKey =
    Ed25519PublicKeyParameters(base64UrlDecode "JrQLj5P_89iXES9-vFgrIy29clF9CC_oPPsw3c5D0bs", 0)

let private ed25519Sign (data: byte[]) =
    let signer = Ed25519Signer()
    signer.Init(true, rfcPrivateKey)
    signer.BlockUpdate(data, 0, data.Length)
    signer.GenerateSignature()

let private ed25519Verify (data: byte[]) (signature: byte[]) =
    let verifier = Ed25519Signer()
    verifier.Init(false, rfcPublicKey)
    verifier.BlockUpdate(data, 0, data.Length)
    verifier.VerifySignature signature

let private ed25519Signer keyId =
    { KeyId = keyId
      Sign = fun data _ -> Task.FromResult(ed25519Sign data) }

[<Fact>]
let ``signature base and Ed25519 signature match RFC 9421 appendix B.2.6`` () =
    let components = [ "date"; "@method"; "@path"; "@authority"; "content-type"; "content-length" ]

    let parameters: MessageSignature.SignatureParams =
        { Components = components
          Created = ValueSome 1618884473L
          Expires = ValueNone
          KeyId = ValueSome "test-key-ed25519"
          Nonce = ValueNone
          Alg = ValueNone }

    let serialized = MessageSignature.serializeParams parameters

    Assert.Equal(
        "(\"date\" \"@method\" \"@path\" \"@authority\" \"content-type\" \"content-length\");created=1618884473;keyid=\"test-key-ed25519\"",
        serialized
    )

    let value =
        function
        | "date" -> ValueSome "Tue, 20 Apr 2021 02:07:55 GMT"
        | "@method" -> ValueSome "POST"
        | "@path" -> ValueSome "/foo"
        | "@authority" -> ValueSome "example.com"
        | "content-type" -> ValueSome "application/json"
        | "content-length" -> ValueSome "18"
        | _ -> ValueNone

    let signatureBase =
        match MessageSignature.signatureBase value components serialized with
        | Ok b -> b
        | Error e -> failwith e

    let expectedBase =
        String.Join(
            "\n",
            [ "\"date\": Tue, 20 Apr 2021 02:07:55 GMT"
              "\"@method\": POST"
              "\"@path\": /foo"
              "\"@authority\": example.com"
              "\"content-type\": application/json"
              "\"content-length\": 18"
              "\"@signature-params\": " + serialized ]
        )

    Assert.Equal(expectedBase, signatureBase)

    Assert.Equal(
        "wqcAqbmYJ2ji2glfAMaRy4gruYYnx2nEFN2HN6jrnDnQCK1u02Gb04v9EDgwUPiu4A0w6vuQv5lIp5WPpBKRCw==",
        Convert.ToBase64String(ed25519Sign (Encoding.UTF8.GetBytes signatureBase))
    )

[<Fact>]
let ``a missing covered component is an error, not an empty line`` () =
    Assert.True(Result.isError (MessageSignature.signatureBase (fun _ -> ValueNone) [ "date" ] "(\"date\")"))

[<Fact>]
let ``the empty body digest is the one Griffin's guide gives`` () =
    Assert.Equal(
        "sha-512=:z4PhNX7vuL3xVChQ1m2AB9Yg5AULVxXcg/SpIdNs6c5H0NE8XYXysP+DGNKHfuwvY7kxvUdBeoGlODJ6+SfaPg==:",
        MessageSignature.contentDigest Array.empty
    )

/// Rebuilds the signature base from a recorded request, the way Griffin's verifier would.
let private baseOf (call: Call) =
    let input = MessageSignature.parseDictionary call.Headers["Signature-Input"]
    let memberText = input[MessageSignature.Label]

    let parameters =
        (MessageSignature.parseParams memberText) |> Result.defaultWith (fun e -> failwith e)

    let path, query =
        match call.Target.IndexOf '?' with
        | -1 -> call.Target, ""
        | i -> call.Target.Substring(0, i), call.Target.Substring i

    let value =
        function
        | "@method" -> ValueSome call.Method
        | "@authority" -> ValueSome "api.griffin.com"
        | "@path" -> ValueSome path
        | "@query" -> ValueSome query
        | "content-type" -> call.ContentHeaders.TryFind "Content-Type" |> Option.map ValueSome |> Option.defaultValue ValueNone
        | "content-length" -> ValueSome(string call.BodyBytes.Length)
        | "date" -> ValueSome call.Headers["Date"]
        | "content-digest" -> ValueSome call.Headers["Content-Digest"]
        | _ -> ValueNone

    parameters,
    match MessageSignature.signatureBase value parameters.Components memberText with
    | Ok b -> b
    | Error e -> failwith e

let private signatureOf (call: Call) =
    let members = MessageSignature.parseDictionary call.Headers["Signature"]

    match MessageSignature.parseByteSequence members[MessageSignature.Label] with
    | Ok s -> s
    | Error e -> failwith e

let private signedClient (handler: FakeGriffinHandler) =
    { Config = { config with Signer = ValueSome(ed25519Signer "hs.test") }
      Http = new HttpClient(handler) }

[<Fact>]
let ``a signed POST covers the body and verifies against what was sent`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> created paymentJson)
        let client = signedClient handler
        let before = DateTimeOffset.UtcNow.ToUnixTimeSeconds()

        let! result =
            Api.createPaymentAsync client "/bank/accounts/ba.1/payments" (ToGriffinAccount "/bank/accounts/ba.2") (Money.gbp 5m) ValueNone ct

        Assert.True(Result.isOk result)
        let call = Assert.Single handler.Calls

        use sha = SHA512.Create()
        Assert.Equal("sha-512=:" + Convert.ToBase64String(sha.ComputeHash call.BodyBytes) + ":", call.Headers["Content-Digest"])

        let parameters, signatureBase = baseOf call

        Assert.Equal<string list>(
            [ "@method"; "@authority"; "@path"; "content-type"; "content-length"; "date"; "content-digest" ],
            parameters.Components
        )

        Assert.Equal(ValueSome "hs.test", parameters.KeyId)
        Assert.Equal(ValueSome "ed25519", parameters.Alg)
        Assert.True(parameters.Created.Value >= before)
        Assert.Equal(parameters.Created.Value + 300L, parameters.Expires.Value)
        Assert.True(Guid.TryParse(parameters.Nonce.Value) |> fst)
        Assert.True(ed25519Verify (Encoding.UTF8.GetBytes signatureBase) (signatureOf call))
    }

[<Fact>]
let ``a signed GET covers the query, empty content headers and the empty body digest`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> ok (pageJson "accounts" [] ValueNone))
        let client = signedClient handler
        let! _ = Api.listBankAccountsAsync client (Api.withQuery "/organizations/og.1/bank/accounts" [ ("page[size]", "2") ]) ct
        let call = Assert.Single handler.Calls

        // Griffin answers missing-required-components unless content-type and content-length are covered.
        Assert.Equal("application/json", call.ContentHeaders["Content-Type"])
        Assert.Equal("0", call.ContentHeaders["Content-Length"])
        Assert.Empty call.BodyBytes
        Assert.Equal(MessageSignature.contentDigest Array.empty, call.Headers["Content-Digest"])
        let parameters, signatureBase = baseOf call

        Assert.Equal<string list>(
            [ "@method"; "@authority"; "@path"; "@query"; "content-type"; "content-length"; "date"; "content-digest" ],
            parameters.Components
        )

        Assert.Contains("\"content-length\": 0", signatureBase)
        Assert.Contains("\"@query\": ?page%5Bsize%5D=2", signatureBase)
        Assert.True(ed25519Verify (Encoding.UTF8.GetBytes signatureBase) (signatureOf call))
    }

[<Fact>]
let ``an unsigned GET sends no content`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> ok """{"organization-url":"/organizations/og.1"}""")
        let! _ = Api.getIndexAsync (clientWith handler) ct
        Assert.Empty (Assert.Single handler.Calls).ContentHeaders
    }

[<Fact>]
let ``a throwing signer is SigningFailed and nothing is sent`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> ok "{}")

        let client =
            { Config =
                { config with
                    Signer =
                        ValueSome
                            { KeyId = "hs.test"
                              Sign = fun _ _ -> Task.FromException<byte[]>(InvalidOperationException "KMS unavailable") } }
              Http = new HttpClient(handler) }

        match! Api.getIndexAsync client ct with
        | Error(SigningFailed e) -> Assert.Contains("KMS unavailable", e.Message)
        | other -> failwith $"%A{other}"

        Assert.Empty handler.Calls
    }

[<Fact>]
let ``dictionary parsing keeps commas and equals signs inside quoted strings and inner lists`` () =
    let parsed =
        MessageSignature.parseDictionary
            "a=(\"date\" \"@method\");keyid=\"x,y=z\";created=1, b=:AQID:"

    Assert.Equal("(\"date\" \"@method\");keyid=\"x,y=z\";created=1", parsed["a"])
    Assert.Equal(":AQID:", parsed["b"])

    match MessageSignature.parseParams parsed["a"] with
    | Ok p ->
        Assert.Equal<string list>([ "date"; "@method" ], p.Components)
        Assert.Equal(ValueSome "x,y=z", p.KeyId)
        Assert.Equal(ValueSome 1L, p.Created)
    | Error e -> failwith e

    Assert.Equal<byte[]>([| 1uy; 2uy; 3uy |], (MessageSignature.parseByteSequence parsed["b"] |> Result.defaultValue [||]))

[<Fact>]
let ``DER ECDSA signatures convert to P1363 that verifies, whatever the integer lengths`` () =
    use key = ECDsa.Create(ECCurve.NamedCurves.nistP521)
    let data = "signature base"B

    for _ in 1..40 do
        let der = key.SignData(data, HashAlgorithmName.SHA512, DSASignatureFormat.Rfc3279DerSequence)

        match MessageSignature.derToP1363 66 der with
        | ValueSome p1363 ->
            Assert.Equal(132, p1363.Length)
            Assert.True(key.VerifyData(data, p1363, HashAlgorithmName.SHA512))
        | ValueNone -> failwith "conversion failed"

    Assert.Equal(ValueNone, MessageSignature.derToP1363 66 [| 0x30uy; 0x01uy |])
    Assert.Equal(ValueNone, MessageSignature.derToP1363 66 (Array.zeroCreate 132))
