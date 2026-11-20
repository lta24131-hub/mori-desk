param([switch]$Check)
# Return the deterministic projection of the unmodified upstream dictionary.
# Every source which might be rewritten differs in exactly one i/l/I glyph
# from a reviewed term. Preserve its lowercase spelling if upstream knows it,
# and preserve each candidate's original frequency and insertion order.
$ErrorActionPreference='Stop'
$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot '../src/OcrTranslationInput.cs'))
$match=[regex]::Match($source,'const string ReviewedUiTerms="([a-z ]+)";')
if(!$match.Success){throw 'Could not locate the reviewed UI vocabulary.'}
$retain=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($term in $match.Groups[1].Value.Split(' ')){
 if($term.Length -lt 5 -or $term.Length -gt 24){continue}
 [void]$retain.Add($term)
 for($i=0;$i -lt $term.Length;$i++){
  if($term[$i] -notin @('i','l')){continue}
  foreach($glyph in @('i','l')){
   [void]$retain.Add($term.Substring(0,$i)+$glyph+$term.Substring($i+1))
  }
 }
}
$rows=@(foreach($line in [IO.File]::ReadLines((Join-Path $PSScriptRoot 'frequency_dictionary_en_82_765.txt'))){
 $split=$line.LastIndexOf(' ');if($split -gt 0 -and $retain.Contains($line.Substring(0,$split))){$line}
})
if(!$rows.Count){throw 'Empty OCR vocabulary projection.'}
if(!$Check){return $rows}
$actual=[IO.File]::ReadAllLines((Join-Path $PSScriptRoot 'ocr-ui-frequency.txt'))
if(![Linq.Enumerable]::SequenceEqual([string[]]$rows,[string[]]$actual)){
 throw 'Stale OCR vocabulary. Regenerate ocr-ui-frequency.txt using Get-CompactOcrLexicon.ps1.'
}
