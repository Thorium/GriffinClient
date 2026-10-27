namespace Griffin

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes

/// An unverified fact about a legal person, checked by a verification. Build one with the Claims module.
type Claim =
    { ClaimType: string
      /// The claim's fields besides claim-type: an object (usually an anonymous record) whose property names are
      /// Griffin's kebab-case names. Null properties are left out.
      Fields: obj }

module Claim =

    /// The claim as Griffin's JSON object, with claim-type added to its fields.
    let toJson (claim: Claim) : obj =
        let json = JsonSerializer.SerializeToNode(claim.Fields, Json.options).AsObject()
        json["claim-type"] <- JsonValue.Create claim.ClaimType
        box json

/// What a legal person does for a living, for the employment claim.
type Employment =
    /// industry is one of Griffin's industry-of-occupation values, e.g. "information-technology"; occupation one of
    /// that industry's occupation values, e.g. "software-engineer" (the occupation field of Griffin's API reference).
    | Employed of industry: string * occupation: string voption
    | SelfEmployed of industry: string * occupation: string voption
    | Retired
    | Unemployed

/// Claims for individuals, the ones Griffin's individual workflows require. Values that Griffin restricts to a
/// list (purposes, sources of funds, industries) take Griffin's strings. Claims.custom covers every other claim type.
module Claims =

    let private claim claimType (fields: obj) = { ClaimType = claimType; Fields = fields }

    /// Any claim type, its fields an object with Griffin's property names, e.g.
    /// custom "alternative-number" {| ``telephone-number`` = "+442012345678" |}. From C#, whose property names
    /// cannot hold hyphens, pass a Dictionary&lt;string, object&gt;.
    let custom (claimType: string) (fields: obj) = claim claimType fields

    let individualIdentity (givenName: string) (middleName: string voption) (surname: string) (dateOfBirth: DateTime) =
        claim
            "individual-identity"
            {| ``given-name`` = givenName
               ``middle-name`` = ValueOption.toObj middleName
               surname = surname
               ``date-of-birth`` = dateOfBirth.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) |}

    let contactDetails (emailAddress: string) =
        claim "contact-details" {| ``email-address`` = emailAddress |}

    /// In international format, e.g. "+447700900123".
    let mobileNumber (number: string) = claim "mobile-number" {| ``mobile-number`` = number |}

    /// A UK (or GG, IM, JE) address. Give a building number, a building name, or both.
    let ukResidence
        (buildingNumber: string voption)
        (buildingName: string voption)
        (streetName: string)
        (city: string)
        (postalCode: string)
        (countryCode: string)
        =
        claim
            "individual-residence"
            {| ``building-number`` = ValueOption.toObj buildingNumber
               ``building-name`` = ValueOption.toObj buildingName
               ``street-name`` = streetName
               city = city
               ``postal-code`` = postalCode
               ``country-code`` = countryCode |}

    /// An address outside the UK.
    let internationalResidence
        (addressLine1: string)
        (addressLine2: string voption)
        (city: string)
        (state: string voption)
        (postalCode: string)
        (countryCode: string)
        =
        claim
            "individual-residence"
            {| ``address-line-1`` = addressLine1
               ``address-line-2`` = ValueOption.toObj addressLine2
               city = city
               state = ValueOption.toObj state
               ``postal-code`` = postalCode
               ``country-code`` = countryCode |}

    /// ISO 3166-1 alpha-2 code.
    let nationality (countryCode: string) = claim "nationality" {| nationality = countryCode |}

    /// A UK bank account the legal person holds elsewhere, which workflows that need it check against the person
    /// (a mismatch declines with bank-account-address-mismatch). sortCode and accountNumber are digits only.
    let verifiedBankAccount (sortCode: string) (accountNumber: string) =
        claim
            "verified-bank-account"
            {| ``uk-domestic-sort-code`` = sortCode
               ``uk-domestic-account-number`` = accountNumber |}

    /// ISO 3166-1 alpha-2 codes.
    let taxResidencies (countryCodes: seq<string>) =
        claim "tax-residencies" {| ``tax-residencies`` = Array.ofSeq countryCodes |}

    let usCitizen (isUsCitizen: bool) = claim "us-citizen" {| ``us-citizen?`` = isUsCitizen |}

    let employment (employment: Employment) =
        let working status (industry: string) (occupation: string voption) =
            box
                {| ``employment-status`` = status
                   ``industry-of-occupation`` = industry
                   occupation = ValueOption.toObj occupation |}

        claim
            "employment"
            (match employment with
             | Employed(industry, occupation) -> working "employed" industry occupation
             | SelfEmployed(industry, occupation) -> working "self-employed" industry occupation
             | Retired -> box {| ``employment-status`` = "retired" |}
             | Unemployed -> box {| ``employment-status`` = "unemployed" |})

    /// Annual income.
    let individualIncome (income: Money) =
        claim "individual-income" {| income = Money.toJson income |}

    let initialDeposit (deposit: Money) =
        claim "initial-deposit" {| ``initial-deposit`` = Money.toJson deposit |}

    /// e.g. [ "savings"; "everyday-spending" ].
    let purposesOfAccount (purposes: seq<string>) =
        claim "individual-purposes-of-account" {| ``individual-purposes-of-account`` = Array.ofSeq purposes |}

    /// e.g. [ "salary-or-bonus" ].
    let sourcesOfFunds (sources: seq<string>) =
        claim "individual-sources-of-funds" {| ``individual-sources-of-funds`` = Array.ofSeq sources |}

    let cashPayments (makesCashPayments: bool) =
        claim "cash-payments" {| ``cash-payments?`` = makesCashPayments |}

    /// ISO 3166-1 alpha-2 codes of the countries payments go to or come from; empty for none.
    let internationalPaymentsCountries (countryCodes: seq<string>) =
        claim "international-payments-countries" {| ``international-payments-countries`` = Array.ofSeq countryCodes |}
