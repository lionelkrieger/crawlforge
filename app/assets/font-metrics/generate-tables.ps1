# Build-time font-metric table generator (provenance tool — NOT used at runtime).
#
# Inputs (downloaded at build time, open-licensed):
#   NimbusSans-Regular.afm  — URW base35, GPL+font exception; metrically compatible
#                             with Helvetica/Arial (Adobe core-font metric clone).
#   Roboto-Regular.ttf      — Google Fonts variable Roboto (default instance =
#                             Regular 400, width 100), SIL OFL 1.1.
#
# Outputs: role-specific SERP profiles with per-character advance widths
# normalized to 1000 units/em. Runtime code only reads the JSON tables.
#
# Re-run: pwsh -File generate-tables.ps1   (from this directory)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Read-U16([byte[]]$b, [int]$o) { return ([int]$b[$o] -shl 8) -bor [int]$b[$o + 1] }
function Read-U32([byte[]]$b, [int]$o) { return ([uint32]$b[$o] -shl 24) -bor ([uint32]$b[$o + 1] -shl 16) -bor ([uint32]$b[$o + 2] -shl 8) -bor [uint32]$b[$o + 3] }

# ---------- AFM (Nimbus Sans / Helvetica / Arial metrics) ----------
# AFM C codes above 127 are MacRoman/StandardEncoding positions, NOT Unicode.
# ASCII (32-126) maps directly; everything else resolves via Adobe glyph names.
$nameToUnicode = [System.Collections.Generic.Dictionary[string, int]]::new([System.StringComparer]::Ordinal)
$entryText = @'
exclamdown:A1 cent:A2 sterling:A3 currency:A4 yen:A5 brokenbar:A6 section:A7 dieresis:A8 copyright:A9 ordfeminine:AA guillemotleft:AB logicalnot:AC registered:AE macron:AF degree:B0 plusminus:B1 twosuperior:B2 threesuperior:B3 acute:B4 mu:B5 paragraph:B6 periodcentered:B7 cedilla:B8 onesuperior:B9 ordmasculine:BA guillemotright:BB onequarter:BC onehalf:BD threequarters:BE questiondown:BF
Agrave:C0 Aacute:C1 Acircumflex:C2 Atilde:C3 Adieresis:C4 Aring:C5 AE:C6 Ccedilla:C7 Egrave:C8 Eacute:C9 Ecircumflex:CA Edieresis:CB Igrave:CC Iacute:CD Icircumflex:CE Idieresis:CF Eth:D0 Ntilde:D1 Ograve:D2 Oacute:D3 Ocircumflex:D4 Otilde:D5 Odieresis:D6 multiply:D7 Oslash:D8 Ugrave:D9 Uacute:DA Ucircumflex:DB Udieresis:DC Yacute:DD Thorn:DE germandbls:DF
agrave:E0 aacute:E1 acircumflex:E2 atilde:E3 adieresis:E4 aring:E5 ae:E6 ccedilla:E7 egrave:E8 eacute:E9 ecircumflex:EA edieresis:EB igrave:EC iacute:ED icircumflex:EE idieresis:EF eth:F0 ntilde:F1 ograve:F2 oacute:F3 ocircumflex:F4 otilde:F5 odieresis:F6 divide:F7 oslash:F8 ugrave:F9 uacute:FA ucircumflex:FB udieresis:FC yacute:FD thorn:FE ydieresis:FF
endash:2013 emdash:2014 quoteleft:2018 quoteright:2019 quotedblleft:201C quotedblright:201D ellipsis:2026 Euro:20AC trademark:2122 fi:FB01 fl:FB02
'@
foreach ($entry in ($entryText -split '\s+' | Where-Object { $_ })) {
    $parts = $entry -split ':'
    $nameToUnicode[$parts[0]] = [Convert]::ToInt32($parts[1], 16)
}
$afmWidths = @{}
foreach ($line in Get-Content (Join-Path $here 'NimbusSans-Regular.afm')) {
    if ($line -match '^C\s+(-?\d+)\s*;\s*WX\s+(\d+)\s*;\s*N\s+(\S+)') {
        $code = [int]$Matches[1]; $wx = [int]$Matches[2]; $name = $Matches[3]
        if ($code -ge 32 -and $code -le 126) { $afmWidths[$code] = $wx }
        elseif ($nameToUnicode.ContainsKey($name)) { $afmWidths[$nameToUnicode[$name]] = $wx }
    }
}
Write-Host "AFM glyphs parsed: $($afmWidths.Count)"

# ---------- TTF (Roboto default instance) ----------
$ttf = [System.IO.File]::ReadAllBytes((Join-Path $here 'Roboto-Regular.ttf'))
$numTables = Read-U16 $ttf 4
$tables = @{}
for ($i = 0; $i -lt $numTables; $i++) {
    $rec = 12 + $i * 16
    $tag = [System.Text.Encoding]::ASCII.GetString($ttf, $rec, 4)
    $tables[$tag] = @{ Offset = [int](Read-U32 $ttf ($rec + 8)); Length = [int](Read-U32 $ttf ($rec + 12)) }
}
$head = $tables['head'].Offset; $unitsPerEm = Read-U16 $ttf ($head + 18)
$maxp = $tables['maxp'].Offset; $numGlyphs = Read-U16 $ttf ($maxp + 4)
$hhea = $tables['hhea'].Offset; $numHMetrics = Read-U16 $ttf ($hhea + 34)
$hmtx = $tables['hmtx'].Offset
$advances = New-Object int[] $numGlyphs
$lastAdvance = 0
for ($g = 0; $g -lt $numGlyphs; $g++) {
    if ($g -lt $numHMetrics) { $lastAdvance = Read-U16 $ttf ($hmtx + $g * 4); $advances[$g] = $lastAdvance }
    else { $advances[$g] = $lastAdvance }
}

# cmap: prefer format 12 (full Unicode), fall back to format 4 (BMP).
$cmap = $tables['cmap'].Offset
$numSub = Read-U16 $ttf ($cmap + 2)
$fmt12 = -1; $fmt4 = -1
for ($i = 0; $i -lt $numSub; $i++) {
    $rec = $cmap + 4 + $i * 8
    $platform = Read-U16 $ttf $rec; $encoding = Read-U16 $ttf ($rec + 2)
    $off = $cmap + [int](Read-U32 $ttf ($rec + 4))
    $format = Read-U16 $ttf $off
    if ($format -eq 12 -and (($platform -eq 3 -and $encoding -eq 10) -or ($platform -eq 0))) { $fmt12 = $off }
    if ($format -eq 4 -and $platform -eq 3 -and $encoding -eq 1 -and $fmt4 -lt 0) { $fmt4 = $off }
}
$ttfWidths = @{}
if ($fmt12 -ge 0) {
    $nGroups = [int](Read-U32 $ttf ($fmt12 + 12))
    for ($i = 0; $i -lt $nGroups; $i++) {
        $g = $fmt12 + 16 + $i * 12
        $start = [int](Read-U32 $ttf $g); $end = [int](Read-U32 $ttf ($g + 4)); $gid = [int](Read-U32 $ttf ($g + 8))
        if ($end -gt 0x2FFFF) { $end = 0x2FFFF }
        for ($c = $start; $c -le $end; $c++) {
            if ($c -ge 32 -and $c -le 0x2FFFF -and ($gid + $c - $start) -lt $numGlyphs) {
                $ttfWidths[$c] = [int][Math]::Round($advances[$gid + $c - $start] * 1000.0 / $unitsPerEm)
            }
        }
    }
    Write-Host "Roboto cmap format 12 groups: $nGroups"
} elseif ($fmt4 -ge 0) {
    $segCount = (Read-U16 $ttf ($fmt4 + 6)) / 2
    $endBase = $fmt4 + 14; $startBase = $endBase + 2 + $segCount * 2
    $deltaBase = $startBase + $segCount * 2; $rangeBase = $deltaBase + $segCount * 2
    for ($s = 0; $s -lt $segCount; $s++) {
        $end = Read-U16 $ttf ($endBase + $s * 2); $start = Read-U16 $ttf ($startBase + $s * 2)
        $delta = Read-U16 $ttf ($deltaBase + $s * 2); $rangeOff = Read-U16 $ttf ($rangeBase + $s * 2)
        for ($c = $start; $c -le $end; $c++) {
            if ($c -eq 0xFFFF) { continue }
            if ($rangeOff -eq 0) { $gid = ($c + $delta) -band 0xFFFF }
            else {
                $addr = $rangeBase + $s * 2 + $rangeOff + ($c - $start) * 2
                $gid = Read-U16 $ttf $addr
                if ($gid -ne 0) { $gid = ($gid + $delta) -band 0xFFFF }
            }
            if ($gid -lt $numGlyphs -and $c -ge 32) {
                $ttfWidths[$c] = [int][Math]::Round($advances[$gid] * 1000.0 / $unitsPerEm)
            }
        }
    }
    Write-Host "Roboto cmap format 4 segments: $segCount"
} else { throw 'No usable cmap subtable found.' }
Write-Host "Roboto codepoints mapped: $($ttfWidths.Count) (unitsPerEm=$unitsPerEm)"

# ---------- Profile emission ----------
# Keep printable ASCII + Latin-1 + common SERP typography; unknown codepoints
# fall back to the profile default width at runtime (documented in README).
function Select-Widths($source) {
    $kept = [ordered]@{}
    foreach ($code in ($source.Keys | Sort-Object)) {
        if (($code -ge 32 -and $code -le 126) -or ($code -ge 160 -and $code -le 255) -or
            $code -in @(0x2013, 0x2014, 0x2018, 0x2019, 0x201C, 0x201D, 0x2026, 0x20AC, 0x2122)) {
            $kept["$code"] = $source[$code]
        }
    }
    return $kept
}
function Emit-Profile($key, $family, $sizePx, $widths, $defaultWidth, $sourceNote) {
    $profile = [ordered]@{
        profileKey     = $key
        fontFamily     = $family
        fontSizePx     = $sizePx
        unitsPerEm     = 1000
        defaultWidth   = $defaultWidth
        widths         = $widths
        provenance     = $sourceNote
    }
    $json = $profile | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText((Join-Path $here "$key.json"), $json, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Wrote $key.json ($($widths.Count) codepoints)"
}

$arialWidths = Select-Widths $afmWidths
$robotoWidths = Select-Widths $ttfWidths
$arialNote = 'Advance widths from NimbusSans-Regular.afm (URW base35, GPL+font exception), metrically compatible with Helvetica/Arial. Normalized to 1000 units/em.'
$robotoNote = 'Advance widths from Roboto[wdth,wght].ttf default instance (Regular 400), Google Fonts, SIL OFL 1.1. Normalized to 1000 units/em from unitsPerEm=2048.'

# Google SERP rendering approximations (documented in README.md):
# desktop title 20px, desktop description 14px; mobile title 18px, mobile description 14px.
Emit-Profile 'arial-desktop-title'       'Arial (Helvetica-metric-compatible)' 20 $arialWidths 500 $arialNote
Emit-Profile 'arial-desktop-description' 'Arial (Helvetica-metric-compatible)' 14 $arialWidths 500 $arialNote
Emit-Profile 'roboto-mobile-title'       'Roboto' 18 $robotoWidths 500 $robotoNote
Emit-Profile 'roboto-mobile-description' 'Roboto' 14 $robotoWidths 500 $robotoNote
Write-Host 'DONE'
