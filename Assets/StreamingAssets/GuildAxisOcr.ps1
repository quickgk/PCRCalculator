# Local Windows PowerShell 5.1 / STA helper. stdout is UTF-8 OCR text; diagnostics go to stderr.
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$ProgressPreference='SilentlyContinue'
function Await-Ocr($operation,[Type]$type) {
    $task=$script:asTask.MakeGenericMethod($type).Invoke($null,@($operation)); $task.Wait(); return $task.Result
}
function Read-Ocr([Drawing.Bitmap]$image,$engine) {
    $bytes=[IO.MemoryStream]::new()
    try {
        $image.Save($bytes,[Drawing.Imaging.ImageFormat]::Png); $bytes.Position=0
        $stream=[System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($bytes)
        try {
            $decoder=Await-Ocr ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
            $sb=Await-Ocr ($decoder.GetSoftwareBitmapAsync([Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8,[Windows.Graphics.Imaging.BitmapAlphaMode]::Ignore)) ([Windows.Graphics.Imaging.SoftwareBitmap])
            try { return Await-Ocr ($engine.RecognizeAsync($sb)) ([Windows.Media.Ocr.OcrResult]) }
            finally { $sb.Dispose() }
        } finally { $stream.Dispose() }
    } finally { $bytes.Dispose() }
}
function Read-Layout($result,[double]$scale) {
    $words=@(foreach ($line in $result.Lines) {
        $items=@($line.Words | Where-Object {$_.BoundingRect.Height -gt 0}); if (!$items.Count) { continue }
        $heights=@($items | ForEach-Object {$_.BoundingRect.Height} | Sort-Object); $height=$heights[[int][Math]::Floor($heights.Count/2)]
        $centers=@($items | Where-Object {$_.BoundingRect.Height -ge .65*$height} | ForEach-Object {$_.BoundingRect.Y+.5*$_.BoundingRect.Height} | Sort-Object)
        $y=$centers[[int][Math]::Floor($centers.Count/2)]
        foreach ($word in $items) { $r=$word.BoundingRect
            [pscustomobject]@{Text=$word.Text; X=$r.X/$scale; Right=($r.X+$r.Width)/$scale; Y=$y/$scale; H=$height/$scale}
        }
    })
    $rows=[Collections.Generic.List[object]]::new()
    foreach ($w in @($words | Sort-Object Y,X)) {
        $target=$null; $best=[double]::MaxValue
        for ($i=$rows.Count-1;$i -ge 0;$i--) {
            $row=$rows[$i]; $d=[Math]::Abs($row.Y-$w.Y)
            if ($d -gt [Math]::Max($row.H,$w.H)*2 -and $i -lt $rows.Count-3) { break }
            if ($d -le [Math]::Min($row.H,$w.H)*0.55 -and $d -lt $best) { $target=$row; $best=$d }
        }
        if ($null -eq $target) { $target=[pscustomobject]@{Y=$w.Y; H=$w.H; Words=[Collections.Generic.List[object]]::new()}; $rows.Add($target) }
        $n=$target.Words.Count; $target.Y=($n*$target.Y+$w.Y)/($n+1); $target.H=($n*$target.H+$w.H)/($n+1); $target.Words.Add($w)
    }
    foreach ($row in @($rows | Sort-Object Y)) {
        $ordered=@($row.Words | Sort-Object X); $s=[Text.StringBuilder]::new(); $prev=$null
        foreach ($w in $ordered) {
            if ($null -ne $prev) {
                $gap=$w.X-$prev.Right; $h=[Math]::Min($w.H,$prev.H)
                if ($gap -gt $h*.85) { [void]$s.Append("`t") }
                elseif ($gap -gt $h*.20 -and $prev.Text -match '[a-z0-9]$' -and $w.Text -match '^[a-z0-9]') { [void]$s.Append(' ') }
            }
            [void]$s.Append($w.Text); $prev=$w
        }
        [pscustomobject]@{Y=$row.Y; H=$row.H; Words=$ordered; Text=$s.ToString()}
    }
}
try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    Add-Type -AssemblyName System.Drawing
    $null=[Windows.Media.Ocr.OcrEngine,Windows.Foundation,ContentType=WindowsRuntime]
    $null=[Windows.Media.Ocr.OcrResult,Windows.Foundation,ContentType=WindowsRuntime]
    $null=[Windows.Graphics.Imaging.BitmapDecoder,Windows.Foundation,ContentType=WindowsRuntime]
    $null=[Windows.Graphics.Imaging.SoftwareBitmap,Windows.Foundation,ContentType=WindowsRuntime]
    $null=[Windows.Graphics.Imaging.BitmapPixelFormat,Windows.Foundation,ContentType=WindowsRuntime]
    $null=[Windows.Graphics.Imaging.BitmapAlphaMode,Windows.Foundation,ContentType=WindowsRuntime]
    $script:asTask=@([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {$_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and $_.GetGenericArguments().Count -eq 1 -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'})[0]
    $langs=@([Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages | Where-Object {$_.LanguageTag -match '^zh(-|$)'} | Sort-Object @{Expression={if ($_.LanguageTag -match 'Hans|CN|SG') {0} else {1}}})
    if (!$langs.Count) { throw '未安装 Windows 中文 OCR 语言。请在 Windows 语言选项中安装中文光学字符识别，或粘贴文字。' }
    $engine=[Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($langs[0]); if (!$engine) { throw '无法启动本机中文 OCR。' }
    $src=$null; $bmp=$null
    try {
        if ($env:GUILD_AXIS_IMAGE) {
            $path=[IO.Path]::GetFullPath($env:GUILD_AXIS_IMAGE)
            if ($path.StartsWith('\\') -or $path -notmatch '\.(png|jpe?g)$') { throw '请选择本机 PNG/JPEG。' }
            if ([IO.FileInfo]::new($path).Length -gt 32MB) { throw '图片超过 32 MB，请裁剪后重试。' }
            $src=[Drawing.Image]::FromFile($path)
        } else {
            Add-Type -AssemblyName System.Windows.Forms; $src=[Windows.Forms.Clipboard]::GetImage()
            if (!$src) { throw '剪贴板没有图片；请复制图片本身，而不是文件路径。' }
        }
        $w=$src.Width; $h=$src.Height
        if ([long]$w*$h -gt 24000000 -or $w -lt 8 -or $h -lt 8) { throw '图片尺寸异常或过大，请裁剪到文字区域。' }
        $limit=[Windows.Media.Ocr.OcrEngine]::MaxImageDimension
        $scale=[Math]::Min(3.0,[Math]::Min($limit/[double][Math]::Max($w,$h),[Math]::Sqrt(8000000.0/($w*$h))))
        $bmp=[Drawing.Bitmap]::new([int][Math]::Floor($w*$scale),[int][Math]::Floor($h*$scale),[Drawing.Imaging.PixelFormat]::Format24bppRgb)
        $g=[Drawing.Graphics]::FromImage($bmp)
        try { $g.Clear([Drawing.Color]::White); $g.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic; $g.DrawImage($src,0,0,$bmp.Width,$bmp.Height) } finally { $g.Dispose() }
        $result=Read-Ocr $bmp $engine
        if ($null -ne $result.TextAngle -and [Math]::Abs($result.TextAngle) -gt 3) { throw '请将图片文字旋转至水平后重试。' }
        $layout=@(Read-Layout $result $scale)
        if (!$layout.Count -or (($layout.Text -join '') -notmatch '[\u3400-\u9fff]')) { throw '未读到中文，请提供清晰原图。' }
        $header=-1
        for ($i=0;$i -lt $layout.Count;$i++) { $t=$layout[$i].Text -replace '\s',''; if ($t -match '(秒数|时间)' -and $t -match '角色|操作|说明') { $header=$i; break } }
        if ($header -ge 0) {
            $head=$layout[$header]; $col=$head.Words[0]; $colEnd=$col.Right+$head.H*1.4; $last=[int]::MaxValue; $ys=@()
            for ($i=$header+1;$i -lt $layout.Count;$i++) {
                $row=$layout[$i]; if ($row.Text -match '^\s*TIME\s*UP\s*$') { break }
                $left=@($row.Words | Where-Object {$_.X -lt $colEnd}); $right=@($row.Words | Where-Object {$_.X -ge $colEnd}); $s=$left.Text -join ''
                if ($s -match '^([0-9]{1,3}|[0-9]:[0-5][0-9])$') {
                    $n=[int]($s -replace ':',''); if ($n -gt $last) { $row.Text='【请核对倒计时】'+$row.Text }; $last=$n
                } elseif ($s -notmatch '^(开局|开场)$') { $row.Text='【请核对秒数或续行】'+$row.Text }
                if (!$right.Count) { $row.Text='【缺少操作】'+$row.Text }
                $ys+=$row.Y
            }
            if (!$ys.Count) { throw '没有读到时间表。' }
            if ($ys.Count -ge 4) {
                $gaps=@(for ($j=1;$j -lt $ys.Count;$j++) { $ys[$j]-$ys[$j-1] }); $sorted=@($gaps | Sort-Object); $pitch=$sorted[[int][Math]::Floor($sorted.Count/2)]
                for ($j=0;$j -lt $gaps.Count;$j++) { if ($gaps[$j] -gt $pitch*1.78) { $layout[$header+$j+2].Text='【请核对上一行是否漏识别】'+$layout[$header+$j+2].Text } }
            }
        }
        if ($header -lt 0) { [Console]::Out.WriteLine('【请核对】未识别完整表头。请对照原图核对行数与时间，或粘贴文字。') }
        [Console]::Out.Write(($layout.Text -join "`n"))
    } finally { if ($bmp) { $bmp.Dispose() }; if ($src) { $src.Dispose() } }
} catch { [Console]::Error.WriteLine($_.Exception.GetBaseException().Message); exit 1 }
