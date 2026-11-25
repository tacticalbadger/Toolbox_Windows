<#
.SYNOPSIS
    Making an ISO disc image, and reading one back: dot-source this file.

.DESCRIPTION
        . tools\disc-image.ps1
        $holds = @{ Toolbox = 'build\release\Toolbox-0.3.1-win-x64' }
        New-DiscImage -Path out.iso -Label 'Toolbox 0.3.1' -Top <folder> -Folders $holds
        Compare-DiscImage -Path out.iso -Label 'Toolbox 0.3.1' -Top <folder> -Folders $holds
        Remove-DiscImage -Path out.iso

    New-DiscImage puts the files of -Top at the top of the disc, and each
    folder of -Folders under the name it is given there. Compare-DiscImage
    says what differs between the disc and those same folders, one line for
    each thing, and nothing when nothing does. Remove-DiscImage deletes an
    image, mounted or not.

    The image is made by the component Windows has for writing discs
    (IMAPI2), and read back by mounting it as Windows does when an .iso is
    double-clicked, but with no drive letter, so nothing appears in File
    Explorer. Neither needs an administrator, or anything installed.

    The same file as in the owner's Heracles-Windows repository, where it
    was written first; keep the two in step.
#>

# Windows hands the finished image over as a COM stream; this writes one to
# a file. (PowerShell cannot call the stream's Read itself.)
$script:DiscImageStreamSource = @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class ToolboxDiscImageStream
{
    public static long Save(object comStream, string path)
    {
        IStream input = (IStream)comStream;
        IntPtr count = Marshal.AllocHGlobal(sizeof(int));
        long total = 0;
        try
        {
            using (FileStream output = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                byte[] buffer = new byte[1 << 20];
                while (true)
                {
                    input.Read(buffer, buffer.Length, count);
                    int read = Marshal.ReadInt32(count);
                    if (read <= 0) break;
                    output.Write(buffer, 0, read);
                    total += read;
                }
            }
        }
        finally { Marshal.FreeHGlobal(count); }
        return total;
    }
}
'@

# The whole path of a file or folder that may not exist yet. Windows mounts
# an image only by its whole path ("the system cannot find the path
# specified" otherwise).
function Resolve-DiscImagePath([string]$path) {
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path).TrimEnd('\')
}

# The longest name of a disc that Windows reads back whole. A longer one is
# taken without a word and cut to this.
$script:DiscLabelLimit = 32

# Delete an image, if it is there. One left mounted by a check that was
# interrupted is in use, and is let go of first.
function Remove-DiscImage {
    param([Parameter(Mandatory = $true)][string]$Path)
    $Path = Resolve-DiscImagePath $Path
    if (-not (Test-Path -LiteralPath $Path)) { return }
    if ((Get-DiskImage -ImagePath $Path).Attached) { Dismount-DiskImage -ImagePath $Path | Out-Null }
    Remove-Item -LiteralPath $Path -Force
}

function New-DiscImage {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label,
        [string]$Top,
        [hashtable]$Folders = @{}
    )
    if ($Label.Length -gt $script:DiscLabelLimit) {
        throw "a disc's name can be $($script:DiscLabelLimit) characters at most; '$Label' is $($Label.Length)"
    }
    $Path = Resolve-DiscImagePath $Path
    Remove-DiscImage -Path $Path
    if (-not ('ToolboxDiscImageStream' -as [type])) { Add-Type -TypeDefinition $script:DiscImageStreamSource }

    $image = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
    $held = @($image)
    $written = $false
    try {
        $image.FreeMediaBlocks = 0          # as large as it needs to be; the default is a CD
        $image.FileSystemsToCreate = 7      # ISO 9660, Joliet and UDF: Windows reads the last, with names as they are
        $image.VolumeName = $Label
        if ($Top) { $image.Root.AddTree((Resolve-DiscImagePath $Top), $false) }
        foreach ($name in $Folders.Keys) {
            $image.Root.AddDirectory($name)
            $folder = $image.Root.Item($name)
            $held += $folder
            $folder.AddTree((Resolve-DiscImagePath $Folders[$name]), $false)
        }
        $result = $image.CreateResultImage()
        $held += $result
        [void][ToolboxDiscImageStream]::Save($result.ImageStream, $Path)
        $written = $true
    }
    finally {
        # Let go of the files that were put in, so they can be moved or deleted.
        foreach ($com in $held) { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($com) }
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
        if (-not $written -and (Test-Path -LiteralPath $Path)) { Remove-Item -LiteralPath $Path -Force }
    }
}

function Compare-DiscImage {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$Label,
        [string]$Top,
        [hashtable]$Folders = @{}
    )
    $Path = Resolve-DiscImagePath $Path

    # What should be on the disc: where on it, and the file it came from.
    # Names are told apart by case, as a disc's are.
    $wanted = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([System.StringComparer]::Ordinal)
    $from = @()
    if ($Top) { $from += , @('', (Resolve-DiscImagePath $Top)) }
    foreach ($name in $Folders.Keys) { $from += , @("$name\", (Resolve-DiscImagePath $Folders[$name])) }
    foreach ($pair in $from) {
        foreach ($file in [System.IO.Directory]::GetFiles($pair[1], '*', 'AllDirectories')) {
            $wanted[$pair[0] + $file.Substring($pair[1].Length + 1)] = $file
        }
    }

    $different = @()
    Mount-DiskImage -ImagePath $Path -StorageType ISO -NoDriveLetter | Out-Null
    try {
        # Timed by a stopwatch: this machine's clock cannot be trusted for a wait.
        $volume = $null
        $waited = [System.Diagnostics.Stopwatch]::StartNew()
        do {
            $volume = Get-DiskImage -ImagePath $Path | Get-Volume
            if ($volume -and $volume.Path) { break }
            Start-Sleep -Milliseconds 200
        } while ($waited.ElapsedMilliseconds -lt 10000)
        if (-not $volume -or -not $volume.Path) { throw "Windows mounted $Path and shows no disc in it" }
        $disc = $volume.Path

        if ($Label -and $volume.FileSystemLabel -cne $Label) {
            $different += "the disc is called '$($volume.FileSystemLabel)', not '$Label'"
        }
        $found = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([System.StringComparer]::Ordinal)
        foreach ($file in [System.IO.Directory]::GetFiles($disc, '*', 'AllDirectories')) { $found[$file.Substring($disc.Length)] = $file }
        foreach ($name in $wanted.Keys) {
            if (-not $found.ContainsKey($name)) { $different += "not on the disc: $name"; continue }
            $there = (Get-FileHash -LiteralPath $found[$name] -Algorithm SHA256).Hash
            $here = (Get-FileHash -LiteralPath $wanted[$name] -Algorithm SHA256).Hash
            if ($there -ne $here) { $different += "not as it was put in: $name" }
        }
        foreach ($name in $found.Keys) {
            if (-not $wanted.ContainsKey($name)) { $different += "on the disc and not put there: $name" }
        }
    }
    finally {
        Dismount-DiskImage -ImagePath $Path | Out-Null
    }
    return $different
}
