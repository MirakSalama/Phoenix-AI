param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Listen', 'Speak')]
    [string]$Action
)

$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName System.Speech

if ($Action -eq 'Speak') {
    $speech = New-Object System.Speech.Synthesis.SpeechSynthesizer
    try {
        $speech.Speak([Console]::In.ReadToEnd())
    } finally {
        $speech.Dispose()
    }
} else {
    $recognizer = New-Object System.Speech.Recognition.SpeechRecognitionEngine
    try {
        $recognizer.LoadGrammar((New-Object System.Speech.Recognition.DictationGrammar))
        $recognizer.SetInputToDefaultAudioDevice()
        $result = $recognizer.Recognize([TimeSpan]::FromSeconds(8))
        if ($result) { [Console]::WriteLine($result.Text) }
    } finally {
        $recognizer.Dispose()
    }
}
