# Windows' own OCR and PDF reader, with no package. Run with Windows PowerShell 5.1, which can reach these Windows parts directly.
# Made-up picture and PDF only. Prints timings and the made-up text it read.
# Usage: powershell -File ocr-pdf.ps1 <folder> <pdf>
param([string]$Dir, [string]$Pdf)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Runtime.WindowsRuntime
[void][Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
[void][Windows.Globalization.Language, Windows.Foundation, ContentType = WindowsRuntime]
[void][Windows.Graphics.Imaging.BitmapDecoder, Windows.Foundation, ContentType = WindowsRuntime]
[void][Windows.Graphics.Imaging.SoftwareBitmap, Windows.Foundation, ContentType = WindowsRuntime]
[void][Windows.Storage.StorageFile, Windows.Foundation, ContentType = WindowsRuntime]
[void][Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Foundation, ContentType = WindowsRuntime]
[void][Windows.Data.Pdf.PdfDocument, Windows.Foundation, ContentType = WindowsRuntime]

$generic = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
function Wait-Op($op, [Type]$t) { $task = $generic.MakeGenericMethod($t).Invoke($null, @($op)); $task.Wait(); $task.Result }
$action = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' } | Select-Object -First 1
function Wait-Act($op) { $action.Invoke($null, @($op)).Wait() }

function Read-Picture($path, $lang) {
    $file = Wait-Op ([Windows.Storage.StorageFile]::GetFileFromPathAsync($path)) ([Windows.Storage.StorageFile])
    $stream = Wait-Op ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    $dec = Wait-Op ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $bmp = Wait-Op ($dec.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
    $eng = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new($lang))
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $res = Wait-Op ($eng.RecognizeAsync($bmp)) ([Windows.Media.Ocr.OcrResult])
    "OCR [$lang] $([math]::Round($sw.Elapsed.TotalMilliseconds)) ms, $($res.Lines.Count) lines"
    foreach ($l in $res.Lines) { '   ' + (($l.Words | ForEach-Object { $_.Text }) -join ' ') }
}

# A made-up screenshot with Arabic, numbers and English.
$lines = 'تقرير المخزون الشهري', 'الصنف: مسمار صلب 10 مم', 'الكمية 1250 قطعة', 'مصنع السادس من أكتوبر', 'Steel bolt M8 total 4,250.75'
$png = Join-Path $Dir 'syn-ocr.png'
$b = New-Object Drawing.Bitmap 900, 330; $g = [Drawing.Graphics]::FromImage($b); $g.Clear([Drawing.Color]::White)
$g.TextRenderingHint = 'ClearTypeGridFit'; $f = New-Object Drawing.Font('Segoe UI', 22)
$fmt = New-Object Drawing.StringFormat; $fmt.FormatFlags = 'DirectionRightToLeft'
$y = 12; foreach ($l in $lines) { $g.DrawString($l, $f, [Drawing.Brushes]::Black, (New-Object Drawing.RectangleF(10, $y, 880, 56)), $fmt); $y += 62 }
$g.Dispose(); $b.Save($png); $b.Dispose()
Read-Picture $png 'ar-SA'
Read-Picture $png 'en-US'

# PDF: open, then draw pages to pictures.
$sw = [Diagnostics.Stopwatch]::StartNew()
$file = Wait-Op ([Windows.Storage.StorageFile]::GetFileFromPathAsync($Pdf)) ([Windows.Storage.StorageFile])
$doc = Wait-Op ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($file)) ([Windows.Data.Pdf.PdfDocument])
"PDF open: $([math]::Round($sw.Elapsed.TotalMilliseconds)) ms, $($doc.PageCount) pages"
$t = New-Object System.Collections.Generic.List[double]
foreach ($i in 0, 1, 2, 50, 100, 150, 199, 3, 4, 5) {
    if ($i -ge $doc.PageCount) { continue }
    $sw.Restart()
    $page = $doc.GetPage($i); $s = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream
    Wait-Act ($page.RenderToStreamAsync($s)); $page.Dispose()
    $t.Add($sw.Elapsed.TotalMilliseconds); if ($i -eq 0) { "first page: $([math]::Round($sw.Elapsed.TotalMilliseconds)) ms, picture $([math]::Round($s.Size / 1024)) KB" }
}
$sorted = $t | Sort-Object; "PDF one page: median $([math]::Round($sorted[[int]($sorted.Count / 2)])) ms, worst $([math]::Round($sorted[-1])) ms"
"process memory: $([math]::Round((Get-Process -Id $PID).PrivateMemorySize64 / 1MB)) MB (includes PowerShell itself)"
