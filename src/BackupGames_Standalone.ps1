$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location -LiteralPath $root
$seven = @("$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe") | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
$par = @("$env:ProgramFiles\MultiPar\par2j64.exe", "${env:ProgramFiles(x86)}\MultiPar\par2j64.exe", "$root\par2j64.exe") | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
if (-not $seven -or -not $par) { throw '7z.exe or par2j64.exe not found. Edit the paths at the top of this script.' }

function Get-Inventory([string]$path) {
    $base = $path.TrimEnd('\') + '\'
    $items = Get-ChildItem -LiteralPath $path -Force -Recurse -ErrorAction Stop
    $output = foreach ($item in $items) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point unsupported: $($item.FullName)" }
        $relative = $item.FullName.Substring($base.Length)
        if ($item.PSIsContainer) {
            "D`t$relative"
        } else {
            $hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256 -ErrorAction Stop).Hash
            "F`t$relative`t$($item.Length)`t$hash"
        }
    }
    return @($output | Sort-Object -CaseSensitive)
}

$failed = $false
# Snapshot target directories before creating temporary extraction directories.
$folders = @(Get-ChildItem -LiteralPath $root -Directory -Force -ErrorAction Stop | Where-Object { $_.Name -notlike '.backup_verify_*' })
foreach ($folder in $folders) {
    $archive = "$($folder.FullName).7z"
    $hashfile = "$archive.sha256"
    $parfile = "$($folder.FullName).par2"
    $temp = Join-Path $root ('.backup_verify_' + [guid]::NewGuid().ToString('N'))
    Write-Host "`n=== $($folder.Name) ==="
    try {
        if (($folder.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Source directory is a reparse point.' }
        if (@(Get-ChildItem -LiteralPath $folder.FullName -Force -ErrorAction Stop).Count -eq 0) { Write-Host 'SKIP: Folder is already empty.'; continue }
        if ((Test-Path -LiteralPath $archive) -or (Test-Path -LiteralPath $hashfile) -or (Test-Path -LiteralPath $parfile) -or @(Get-ChildItem -LiteralPath $root -File -Filter "$($folder.Name).vol*.par2").Count -gt 0) {
            throw 'Existing output detected; refusing to overwrite.'
        }
        $before = @(Get-Inventory $folder.FullName)
        # Include folder itself in the archive, so extracted data resides at temp\FolderName.
        & $seven a -t7z -mx=9 -m0=lzma2 -md=1g -ms=on -mmt=2 -bb1 -- $archive $folder.Name
        if ($LASTEXITCODE -ne 0) { throw "7z compression exit code: $LASTEXITCODE" }
        & $seven t -- $archive
        if ($LASTEXITCODE -ne 0) { throw "7z test exit code: $LASTEXITCODE" }
        New-Item -ItemType Directory -Path $temp | Out-Null
        & $seven x -y "-o$temp" -- $archive
        if ($LASTEXITCODE -ne 0) { throw "7z extraction exit code: $LASTEXITCODE" }
        $extracted = Join-Path $temp $folder.Name
        if (-not (Test-Path -LiteralPath $extracted -PathType Container)) { throw 'Extracted root folder missing.' }
        $after = @(Get-Inventory $extracted)
        if ($before.Count -ne $after.Count) { throw 'Inventory item count mismatch.' }
        for ($i=0; $i -lt $before.Count; $i++) {
            if ($before[$i] -cne $after[$i]) { throw "Hash or path mismatch: $($before[$i])" }
        }
        $current = @(Get-Inventory $folder.FullName)
        if ($before.Count -ne $current.Count) { throw 'Source changed during processing.' }
        for ($i=0; $i -lt $before.Count; $i++) {
            if ($before[$i] -cne $current[$i]) { throw 'Source changed during processing.' }
        }
        $sha = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        $shaLine = "$sha *$($folder.Name).7z`n"
        [IO.File]::WriteAllText($hashfile, $shaLine, [Text.UTF8Encoding]::new($false))
        & $par c /rr10 $parfile $archive
        if ($LASTEXITCODE -ne 0) { throw "PAR2 creation exit code: $LASTEXITCODE" }
        & $par v $parfile
        if ($LASTEXITCODE -ne 0) { throw "PAR2 verification exit code: $LASTEXITCODE" }
        # Recheck immediately before deletion, since source may have changed during PAR2 creation.
        $latest = @(Get-Inventory $folder.FullName)
        if ($before.Count -ne $latest.Count) { throw 'Source changed before deletion.' }
        for ($i=0; $i -lt $before.Count; $i++) {
            if ($before[$i] -cne $latest[$i]) { throw 'Source changed before deletion.' }
        }
        # The folder remains; remove its immediate children, recursively.
        foreach ($child in @(Get-ChildItem -LiteralPath $folder.FullName -Force -ErrorAction Stop)) {
            Remove-Item -LiteralPath $child.FullName -Recurse -Force -ErrorAction Stop
        }
        Write-Host "SUCCESS: $($folder.Name) (original folder kept empty)" -ForegroundColor Green
    } catch {
        $failed = $true
        Write-Warning "FAILED: $($folder.Name): $($_.Exception.Message) (original folder retained unless deletion already started)"
    } finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
if ($failed) { exit 1 } else { exit 0 }

# SIG # Begin signature block
# MIIHNgYJKoZIhvcNAQcCoIIHJzCCByMCAQExDzANBglghkgBZQMEAgEFADB5Bgor
# BgEEAYI3AgEEoGswaTA0BgorBgEEAYI3AgEeMCYCAwEAAAQQH8w7YFlLCE63JNLG
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCAlR28syY3YihJH
# x3KZxuZshseJQEks20X/szmKEVsnS6CCBCAwggQcMIIChKADAgECAhAXPtKmcpSj
# p0MPghE5ohAWMA0GCSqGSIb3DQEBCwUAMCYxJDAiBgNVBAMMG1BlcnNvbmFsIFBv
# d2VyU2hlbGwgU2NyaXB0czAeFw0yNjEwMTIwMTM2MTNaFw0zNjEwMTIwMTQ2MTJa
# MCYxJDAiBgNVBAMMG1BlcnNvbmFsIFBvd2VyU2hlbGwgU2NyaXB0czCCAaIwDQYJ
# KoZIhvcNAQEBBQADggGPADCCAYoCggGBAKpRqN4oOw/e23wAAWlcxRs6okgikR2l
# dCTbCuUhFUfiNmfzPQNhSY+sCkVRe1bPUkfSalfhI18QG9UlDU4xW3gaKFA9nTf2
# LKbNAa304VHDHZo137iL0zS7WOn5rXttMZQMsDq1O+/7pbxW1zAfi2Nr8TpPujom
# sOx1mByXYieX2Q6xwIjYCZH0c4JZj/AsvP95rX8bREx3S32b/zMp4nJgwkurQkv3
# WJlaXV3ZmjzZv6ej3VRwhnOvn9Rr1TA+rVP5w2e83MctDNV2GdMzP0IjNJgWrMkF
# qtq/JpPJVN1C/4b7DdX4rICVXj9Pr/M6elvYbXhlNeI6FsSURpVpXV2eKji/mrUe
# W9Scveio3zCsKPB8uFRFeHF3EgmZkHNDVRP2dB252JvgQv0U1xz2c2exxcLpD4Ju
# zpS+XsAGJpJLKnJTt15HR6jMMRx2vKkM/L7OgA+6xMvirvhK+cwwc2pk9TXSXWDJ
# K3rT3FzEwFhlENLSMpDg8s3X86vJUuPtfQIDAQABo0YwRDAOBgNVHQ8BAf8EBAMC
# B4AwEwYDVR0lBAwwCgYIKwYBBQUHAwMwHQYDVR0OBBYEFOG5RIunA7886zdtXEwe
# V3V4W3m9MA0GCSqGSIb3DQEBCwUAA4IBgQCEJLD3nWY3gKMPjpX1VhvjdvBFbZk2
# 698A1ygWozALZ1J0qLKNQ8RqtuGvTYssfug6oOrnDDAMXVhxG3UgEfiTYM9qSZ81
# +V+QnKuxLwN7DQYmDVS3lwRw7/PURHxmo4o/TkyyboqQyKgUQeRH432UMPGs9ZOR
# WycDjaB1Ovh1/5APux4jpQb8uMY+X1Peen5VEn5i6mlVk93/R3EF1HHClJcgjHy1
# 6GXv5xDs/Cpu/O3AW0Ram5UQy8yC/jXAn9N/SzxDYmqhTpTh/Q5f9Z3GUTPFJJ0y
# mLSwpEpYly/lrNTd2H2bXS626QnpG+0yb4l7MrcdvcKTgQerQmc1SkLKeQNPS2uV
# LLZh2e7viKCfBKw2M3Fsqk3+8zesj3KBuY8xtWlbXWTBqqXfFJSt9lvnx7XDtxxs
# IKm5SoAs0TDXmSvKPqN9XACRuXED+7kH4o5urlpXSmuAaGT82SEalygOYSLVZI41
# uD4NkglA1y0Kyyrde6ijwHzOp0cdkIanv60xggJsMIICaAIBATA6MCYxJDAiBgNV
# BAMMG1BlcnNvbmFsIFBvd2VyU2hlbGwgU2NyaXB0cwIQFz7SpnKUo6dDD4IROaIQ
# FjANBglghkgBZQMEAgEFAKCBhDAYBgorBgEEAYI3AgEMMQowCKACgAChAoAAMBkG
# CSqGSIb3DQEJAzEMBgorBgEEAYI3AgEEMBwGCisGAQQBgjcCAQsxDjAMBgorBgEE
# AYI3AgEVMC8GCSqGSIb3DQEJBDEiBCAgCTdNJsUYaMdMbk10MDCkymiCQMNh+aca
# aGGk4PU+PTANBgkqhkiG9w0BAQEFAASCAYCo0FUOndTWUeYJkTsdcOyDdh6G+vwM
# 1Ft+cE1dS617XDjLsB+H3HdH9myPeS8mi40n38gS7YjnZn4DvkvcZ4TllZOstG3u
# 2kTl0GtxA2t0REdXQ1pvcFmXYCJreJ59bScxGogsm4C3YoBQpBiZcRRstgurAbYN
# XXE2RaHG+iL0HRXrAmfC0jKof7MnJmLHQzV9KA0vaNNepranj16r4uUy+NvGsjie
# jQAF6F3I9lqHEJ+QvCnV41aPZtdQj8soosAWxwv++1aOJ0iUaeFnr1xfCZetXyEH
# Yi7Q4EVA3VQHzFNwUKlQ8fWhX/wIo5zeXsXDzCKw5/Y+rcvw2zNKydIdWK2zQpbm
# FrPd7HVTwOYaH+t3h/U0cb+QqEy2SOpS8d/xhA8p1pm3Tovmpdo6s3cBOZk6ESFM
# KDp+HJeC7nZ1D/xqvB1htRuocV9XqVu45Je6w2fF7wfJYwwL1872CaAXXULvwccG
# X/RAINgqitkRKZcpuavmuYnyEdIK1qletG4=
# SIG # End signature block
