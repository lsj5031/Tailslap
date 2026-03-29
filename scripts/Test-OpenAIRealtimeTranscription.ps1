param(
    [string]$RealtimeUrl = "ws://localhost:18000/v1/realtime?intent=transcription",
    [string]$Phrase = "hello world this is a realtime transcription test from tailslap"
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Speech

function New-TestAudioPcm16 {
    param([string]$Text)

    $tempWav = Join-Path $env:TEMP "tailslap-realtime-test.wav"
    if (Test-Path $tempWav) {
        Remove-Item $tempWav -Force
    }

    $format = [System.Speech.AudioFormat.SpeechAudioFormatInfo]::new(
        24000,
        [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen,
        [System.Speech.AudioFormat.AudioChannel]::Mono
    )

    $synth = [System.Speech.Synthesis.SpeechSynthesizer]::new()
    try {
        $synth.Rate = 0
        $synth.Volume = 100
        $synth.SetOutputToWaveFile($tempWav, $format)
        $synth.Speak($Text)
    }
    finally {
        $synth.Dispose()
    }

    [byte[]]$wav = [System.IO.File]::ReadAllBytes($tempWav)
    if ($wav.Length -lt 45) {
        throw "Generated WAV file is too small."
    }

    return $wav[44..($wav.Length - 1)]
}

function Send-JsonMessage {
    param(
        [System.Net.WebSockets.ClientWebSocket]$Socket,
        [string]$Json,
        [System.Threading.CancellationToken]$CancellationToken
    )

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Json)
    $segment = [ArraySegment[byte]]::new($bytes)
    $sendTask = $Socket.SendAsync(
        $segment,
        [System.Net.WebSockets.WebSocketMessageType]::Text,
        $true,
        $CancellationToken
    )
    [void]$sendTask.GetAwaiter().GetResult()
}

$pcm = New-TestAudioPcm16 -Text $Phrase
$socket = [System.Net.WebSockets.ClientWebSocket]::new()
$cts = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(30))

try {
    [void]$socket.ConnectAsync([Uri]$RealtimeUrl, $cts.Token).GetAwaiter().GetResult()

    $sessionUpdate = @{
        type = "transcription_session.update"
        input_audio_format = "pcm16"
        input_audio_transcription = @{
            model = "gpt-4o-transcribe"
            language = "en"
        }
        turn_detection = $null
        input_audio_noise_reduction = @{
            type = "near_field"
        }
    } | ConvertTo-Json -Depth 6 -Compress
    Send-JsonMessage -Socket $socket -Json $sessionUpdate -CancellationToken $cts.Token

    $chunkSize = 8192
    for ($offset = 0; $offset -lt $pcm.Length; $offset += $chunkSize) {
        $count = [Math]::Min($chunkSize, $pcm.Length - $offset)
        $chunk = [byte[]]::new($count)
        [Array]::Copy($pcm, $offset, $chunk, 0, $count)

        $appendEvent = @{
            type = "input_audio_buffer.append"
            audio = [Convert]::ToBase64String($chunk)
        } | ConvertTo-Json -Compress
        Send-JsonMessage -Socket $socket -Json $appendEvent -CancellationToken $cts.Token
        Start-Sleep -Milliseconds 100
    }

    $commitEvent = @{ type = "input_audio_buffer.commit" } | ConvertTo-Json -Compress
    Send-JsonMessage -Socket $socket -Json $commitEvent -CancellationToken $cts.Token

    $buffer = [byte[]]::new(16384)
    $messageBuilder = [System.Text.StringBuilder]::new()
    $events = [System.Collections.Generic.List[string]]::new()
    $transcript = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(20)

    while ([DateTime]::UtcNow -lt $deadline -and $socket.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
        $receiveCts = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(2))
        try {
            $receiveTask = $socket.ReceiveAsync([ArraySegment[byte]]::new($buffer), $receiveCts.Token)
            $result = $receiveTask.GetAwaiter().GetResult()
        }
        catch [System.OperationCanceledException] {
            $receiveCts.Dispose()
            continue
        }
        finally {
            if ($null -ne $receiveCts) {
                $receiveCts.Dispose()
            }
        }

        if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
            break
        }

        if ($result.MessageType -ne [System.Net.WebSockets.WebSocketMessageType]::Text) {
            continue
        }

        [void]$messageBuilder.Append([System.Text.Encoding]::UTF8.GetString($buffer, 0, $result.Count))
        if (-not $result.EndOfMessage) {
            continue
        }

        $json = $messageBuilder.ToString()
        [void]$messageBuilder.Clear()
        $events.Add($json)

        try {
            $eventObject = $json | ConvertFrom-Json
            if ($eventObject.type -eq "conversation.item.input_audio_transcription.completed" -and $eventObject.transcript) {
                $transcript = [string]$eventObject.transcript
                break
            }

            if ($eventObject.type -eq "transcript.text.done" -and $eventObject.text) {
                $transcript = [string]$eventObject.text
                break
            }

            if ($eventObject.type -eq "error") {
                break
            }
        }
        catch {
        }
    }

    [pscustomobject]@{
        realtime_url = $RealtimeUrl
        phrase = $Phrase
        audio_bytes = $pcm.Length
        event_count = $events.Count
        transcript = $transcript
        events = $events
    } | ConvertTo-Json -Depth 8
}
finally {
    if ($socket.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
        $closeTask = $socket.CloseAsync(
            [System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure,
            "done",
            [System.Threading.CancellationToken]::None
        )
        [void]$closeTask.GetAwaiter().GetResult()
    }

    $socket.Dispose()
    $cts.Dispose()
}
