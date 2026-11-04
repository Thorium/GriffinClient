/// Request signers from an Ed25519 private key held in the process, for when no key store or KMS can sign.
module Griffin.Ed25519Key

open System
open System.Text.RegularExpressions
open System.Threading.Tasks
open Org.BouncyCastle.Crypto.Parameters
open Org.BouncyCastle.Crypto.Signers
open Org.BouncyCastle.Security

let private read (privateKey: string) =
    try
        let base64 = Regex.Replace(privateKey, @"-----[A-Z ]+-----|\\[rn]|\s", "")

        match PrivateKeyFactory.CreateKey(Convert.FromBase64String base64) with
        | :? Ed25519PrivateKeyParameters as key -> Ok key
        | other -> Error $"it is {other.GetType().Name}"
    with error ->
        Error error.Message

/// A signer with the key id Griffin assigned when the public key was registered, and the PKCS#8 private key as PEM (line
/// breaks optional, `\n` escapes allowed, as in a JSON secret) or bare base64. InvalidRequest for an empty key id or
/// anything but an Ed25519 private key.
let signerOf (keyId: string) (privateKey: string) : Result<RequestSigner, GriffinFailure> =
    if String.IsNullOrWhiteSpace keyId then
        Error(InvalidRequest "The signing key id is empty.")
    else
        match read privateKey with
        | Error what ->
            Error(InvalidRequest $"The signing key must be an Ed25519 private key in PKCS#8 (PEM or base64): {what}")
        | Ok key ->
            Ok
                { KeyId = keyId.Trim()
                  Sign =
                    fun data _ ->
                        let signer = Ed25519Signer()
                        signer.Init(true, key)
                        signer.BlockUpdate(data, 0, data.Length)
                        Task.FromResult(signer.GenerateSignature()) }
