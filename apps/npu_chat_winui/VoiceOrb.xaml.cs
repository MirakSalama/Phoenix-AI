using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace NpuChat;

internal enum OrbMode { Listening, Thinking, Cloud, Speaking }

public sealed partial class VoiceOrb : UserControl
{
    private static readonly Color Mint = Color.FromArgb(255, 67, 227, 220);
    private static readonly Color Blue = Color.FromArgb(255, 87, 148, 255);
    private static readonly Color Violet = Color.FromArgb(255, 158, 113, 255);
    private static readonly Color Ember = Color.FromArgb(255, 255, 139, 82);
    private static readonly Color Gold = Color.FromArgb(255, 255, 200, 111);
    private static readonly Color Rose = Color.FromArgb(255, 255, 103, 112);
    private static readonly Color DeepBlue = Color.FromArgb(255, 23, 34, 91);
    private static readonly Color[] SpeechColors = [Ember, Gold, Rose];
    private readonly Stopwatch drift = new();
    private readonly UISettings systemSettings = new();
    private OrbMode mode;
    private bool running;
    private IReadOnlyList<double> levels = [];
    private long playbackStart;
    private double smoothedLevel;

    public VoiceOrb()
    {
        InitializeComponent();
        Unloaded += (_, _) => SetActive(false, mode);
    }

    internal void SetActive(bool active, OrbMode state, IReadOnlyList<double>? playback = null, long started = 0, bool animate = true)
    {
        mode = state;
        levels = playback ?? [];
        playbackStart = started;
        Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        animate = active && animate && systemSettings.AnimationsEnabled;
        if (running != animate)
        {
            running = animate;
            if (animate)
            {
                drift.Start();
                CompositionTarget.Rendering += Render;
            }
            else
            {
                CompositionTarget.Rendering -= Render;
                drift.Stop();
                smoothedLevel = 0;
            }
        }
        if (active) Draw(animate ? drift.Elapsed.TotalSeconds : 0);
    }

    private void Render(object? sender, object args) => Draw(drift.Elapsed.TotalSeconds);

    private void Draw(double time)
    {
        double energy = 0;
        Color color = mode switch
        {
            OrbMode.Listening => Mint,
            OrbMode.Thinking => Blue,
            OrbMode.Cloud => Violet,
            _ => Ember
        };
        if (mode == OrbMode.Speaking && running)
        {
            double playback = playbackStart == 0 ? time : Stopwatch.GetElapsedTime(playbackStart).TotalSeconds;
            int frame = (int)(playback / 0.05);
            energy = levels.Count == 0 ? 0.33 + 0.25 * Math.Sin(playback * 9) :
                frame < levels.Count ? Math.Clamp(levels[frame], 0, 1) : 0;
            double hue = playback * 0.95 % SpeechColors.Length;
            int index = (int)hue;
            color = Blend(SpeechColors[index], SpeechColors[(index + 1) % SpeechColors.Length], hue - index);
        }
        smoothedLevel += (energy - smoothedLevel) * 0.25;
        FloatMotion.TranslateY = running ? -4.5 * Math.Sin(time * 2.4) : 0;
        FloatMotion.TranslateX = running ? 2.5 * Math.Sin(time * 1.6) : 0;
        PulseMotion.ScaleX = PulseMotion.ScaleY = 1 + 0.035 * Math.Sin(time * 2.9) + 0.10 * smoothedLevel;
        EmberRim.Color = mode == OrbMode.Speaking ? color : Color.FromArgb(175, 255, 169, 110);
        Core.Color = mode == OrbMode.Speaking ? Blue : Blend(Blue, color, 0.55);
        Rim.Color = Blend(color, DeepBlue, 0.7);
        Halo.Color = Color.FromArgb((byte)(94 + 70 * smoothedLevel), color.R, color.G, color.B);
        HaloEdge.Color = Color.FromArgb((byte)(32 + 52 * smoothedLevel), color.R, color.G, color.B);
    }

    private static Color Blend(Color from, Color to, double progress) => Color.FromArgb(255,
        (byte)(from.R + (to.R - from.R) * progress),
        (byte)(from.G + (to.G - from.G) * progress),
        (byte)(from.B + (to.B - from.B) * progress));
}
