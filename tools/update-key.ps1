<#
.SYNOPSIS
    The key that updates of Toolbox are signed with.

.DESCRIPTION
        tools\update-key.ps1 create               make the key on this PC (once, ever)
        tools\update-key.ps1 public               print its public half, as the app carries it
        tools\update-key.ps1 check                compare it with the public half in the app's source
        tools\update-key.ps1 sign   -File <path>  write <path>.sig
        tools\update-key.ps1 verify -File <path>  check <path> against <path>.sig
        tools\update-key.ps1 export -To <path>    write the private half to a file, for safekeeping
        tools\update-key.ps1 import -From <path>  take the key from such a file (another PC)

    An installed copy of Toolbox takes an update only if the release's
    update.json carries a signature made with this key (ECDSA, P-256,
    SHA-256); the public half is compiled into the app (Updates.cs). Whoever
    has the private half can make every installed copy install anything, so:

      - It lives in this Windows account's key store (not in a file, never
        in the repository) under the name below.
      - There is one key. A second one is made for nothing short of a lost
        one, and then copies in the field cannot be told: they have to be
        replaced by hand.
      - Keep a copy in a password manager: "export" writes the file to put
        there. Delete the file afterwards.

    -Name is for tests (the update rehearsal makes a key of its own and
    deletes it); leave it alone otherwise.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('create', 'public', 'check', 'sign', 'verify', 'export', 'import', 'delete')]
    [string]$Action,
    [string]$File,
    [string]$To,
    [string]$From,
    [string]$Name = 'Toolbox Update Key',
    [string]$PublicKey
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
Add-Type -AssemblyName System.Core

$Root = Split-Path -Parent $PSScriptRoot
$Provider = [System.Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider

function Get-Key {
    if (-not [System.Security.Cryptography.CngKey]::Exists($Name, $Provider)) {
        throw "this Windows account has no key named '$Name'. On the PC releases are made on: tools\update-key.ps1 create. On another PC: tools\update-key.ps1 import -From <file>."
    }
    return [System.Security.Cryptography.CngKey]::Open($Name, $Provider)
}

# The public half as the app carries it: the point's X and Y, 32 bytes each, in Base64.
function Get-PublicText($key) {
    $blob = $key.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    # BCRYPT_ECCKEY_BLOB: magic (4 bytes), key length (4 bytes), X, Y.
    $length = [BitConverter]::ToInt32($blob, 4)
    if ($length -ne 32 -or $blob.Length -ne 72) { throw 'this is not a P-256 key' }
    return [Convert]::ToBase64String($blob, 8, 64)
}

# The public half written in the app's source.
function Get-SourcePublicText {
    $source = [System.IO.File]::ReadAllText((Join-Path $Root 'app\Toolbox\Support\Updates.cs'))
    if ($source -notmatch 'PublicKey\s*=\s*"([A-Za-z0-9+/=]*)"') { throw 'no PublicKey in app\Toolbox\Support\Updates.cs' }
    return $Matches[1]
}

function New-Verifier([string]$publicText) {
    $point = [Convert]::FromBase64String($publicText)
    if ($point.Length -ne 64) { throw 'a public key is 64 bytes in Base64' }
    $blob = New-Object byte[] 72
    [BitConverter]::GetBytes([int]0x31534345).CopyTo($blob, 0)   # "ECS1": ECDSA P-256, public
    [BitConverter]::GetBytes([int]32).CopyTo($blob, 4)
    [Array]::Copy($point, 0, $blob, 8, 64)
    $key = [System.Security.Cryptography.CngKey]::Import($blob, [System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    return New-Object System.Security.Cryptography.ECDsaCng $key
}

function New-KeyParameters {
    $parameters = New-Object System.Security.Cryptography.CngKeyCreationParameters
    $parameters.Provider = $Provider
    # Exportable, so that a copy can be kept somewhere safe.
    $parameters.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::AllowExport -bor [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
    $parameters.KeyUsage = [System.Security.Cryptography.CngKeyUsages]::Signing
    return $parameters
}

switch ($Action) {
    'create' {
        if ([System.Security.Cryptography.CngKey]::Exists($Name, $Provider)) {
            throw "a key named '$Name' is there already. Nothing was changed: a new key would cut every installed copy off from updates."
        }
        $key = [System.Security.Cryptography.CngKey]::Create([System.Security.Cryptography.CngAlgorithm]::ECDsaP256, $Name, (New-KeyParameters))
        Write-Host "created '$Name' in this account's key store"
        Write-Host 'public half (goes into app\Toolbox\Support\Updates.cs as PublicKey):'
        Write-Output (Get-PublicText $key)
        Write-Host 'next: tools\update-key.ps1 export -To <file>, put that file in a password manager, delete it'
    }
    'public' { Write-Output (Get-PublicText (Get-Key)) }
    'check' {
        $mine = Get-PublicText (Get-Key)
        $source = Get-SourcePublicText
        if ($mine -ne $source) { throw "the key in this account's store is not the one the app trusts (Updates.cs has $source). A release signed here would be refused by every installed copy." }
        Write-Host 'ok: the key in this account is the one the app trusts'
    }
    'sign' {
        if (-not $File) { throw 'give -File' }
        $data = [System.IO.File]::ReadAllBytes((Resolve-Path $File).Path)
        $signer = New-Object System.Security.Cryptography.ECDsaCng (Get-Key)
        $signer.HashAlgorithm = [System.Security.Cryptography.CngAlgorithm]::Sha256
        $signature = $signer.SignData($data)
        [System.IO.File]::WriteAllText(((Resolve-Path $File).Path + '.sig'), [Convert]::ToBase64String($signature) + "`n", [System.Text.Encoding]::ASCII)
        Write-Host "signed: $File.sig"
    }
    'verify' {
        if (-not $File) { throw 'give -File' }
        $path = (Resolve-Path $File).Path
        $data = [System.IO.File]::ReadAllBytes($path)
        $signature = [Convert]::FromBase64String([System.IO.File]::ReadAllText($path + '.sig').Trim())
        $text = if ($PublicKey) { $PublicKey } else { Get-SourcePublicText }
        $verifier = New-Verifier $text
        $verifier.HashAlgorithm = [System.Security.Cryptography.CngAlgorithm]::Sha256
        if (-not $verifier.VerifyData($data, $signature)) { throw "$File does not carry a signature of the key the app trusts" }
        Write-Host "ok: $File is signed with the key the app trusts"
    }
    'export' {
        if (-not $To) { throw 'give -To <file>' }
        if (Test-Path $To) { throw "$To exists already" }
        # Windows' own form for such a key (BCRYPT_ECCKEY_BLOB with the
        # private number): the one form the key store takes back under a name.
        $blob = (Get-Key).Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob)
        $lines = @('-----BEGIN TOOLBOX UPDATE KEY-----') + ([Convert]::ToBase64String($blob) -split '(.{64})' | Where-Object { $_ }) + '-----END TOOLBOX UPDATE KEY-----'
        [System.IO.File]::WriteAllText($To, ($lines -join "`n") + "`n", [System.Text.Encoding]::ASCII)
        Write-Host "written: $To"
        Write-Host 'This file is the key. Put it in a password manager and delete it here.'
    }
    'import' {
        if (-not $From) { throw 'give -From <file>' }
        if ([System.Security.Cryptography.CngKey]::Exists($Name, $Provider)) { throw "a key named '$Name' is there already" }
        $text = ([System.IO.File]::ReadAllText((Resolve-Path $From).Path) -replace '-----[A-Z ]+-----', '') -replace '\s', ''
        $parameters = New-KeyParameters
        $property = New-Object System.Security.Cryptography.CngProperty ([System.Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob.Format, [Convert]::FromBase64String($text), [System.Security.Cryptography.CngPropertyOptions]::None)
        $parameters.Parameters.Add($property)
        $key = [System.Security.Cryptography.CngKey]::Create([System.Security.Cryptography.CngAlgorithm]::ECDsaP256, $Name, $parameters)
        Write-Host "imported '$Name'; public half:"
        Write-Output (Get-PublicText $key)
    }
    'delete' {
        if ($Name -eq 'Toolbox Update Key') { throw 'the real update key is not deleted by a script' }
        if ([System.Security.Cryptography.CngKey]::Exists($Name, $Provider)) {
            ([System.Security.Cryptography.CngKey]::Open($Name, $Provider)).Delete()
            Write-Host "deleted '$Name'"
        }
    }
}
