using System;
using System.Collections.Generic;
using Godot;

namespace IH.Client;

/// <summary>Sound effects synthesised at start-up (the project ships no audio files).</summary>
public partial class Sfx : Node
{
    public enum Id { Click, Move, Combat, Turn, Tech, Build, Error, City }

    private readonly Dictionary<Id, AudioStreamWav> _streams = new();
    private readonly AudioStreamPlayer[] _players = new AudioStreamPlayer[6];
    private int _next;
    public bool Muted;
    private const int Rate = 22050;

    public override void _Ready()
    {
        for (int i = 0; i < _players.Length; i++) { _players[i] = new AudioStreamPlayer { VolumeDb = -8 }; AddChild(_players[i]); }
        _streams[Id.Click] = Make(0.05, t => Tone(t, 900) * Decay(t, 0.05));
        _streams[Id.Move] = Make(0.12, t => (Tone(t, 150) + 0.5 * Tone(t, 75)) * Decay(t, 0.12));
        _streams[Id.Combat] = Make(0.35, t => (Noise() * 0.8 + Tone(t, 90)) * Decay(t, 0.35));
        _streams[Id.Turn] = Make(0.45, t => (t < 0.18 ? Tone(t, 523) : Tone(t, 784)) * Decay(t % 0.18, 0.3) * 0.8);
        _streams[Id.Tech] = Make(0.6, t => Tone(t, t < 0.15 ? 523 : t < 0.3 ? 659 : t < 0.45 ? 784 : 1047) * Decay(t % 0.15, 0.25) * 0.8);
        _streams[Id.Build] = Make(0.3, t => (Tone(t, 660) + 0.4 * Tone(t, 1320)) * Decay(t, 0.3) * 0.7);
        _streams[Id.City] = Make(0.7, t => (Tone(t, 392) + Tone(t, 494) + Tone(t, 587)) / 3 * Decay(t, 0.7));
        _streams[Id.Error] = Make(0.22, t => Math.Sign(Tone(t, 140)) * 0.4 * Decay(t, 0.22));
    }

    private static readonly Random Rnd = new(1);
    private static double Tone(double t, double hz) => Math.Sin(t * hz * Math.Tau);
    private static double Noise() => Rnd.NextDouble() * 2 - 1;
    private static double Decay(double t, double length) => Math.Exp(-4.5 * t / length);

    private static AudioStreamWav Make(double seconds, Func<double, double> wave)
    {
        int n = (int)(seconds * Rate);
        var data = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            double fadeIn = Math.Min(1, i / 60.0); // avoids a click at the start
            short v = (short)(Math.Clamp(wave(t) * fadeIn, -1, 1) * 26000);
            data[i * 2] = (byte)(v & 0xFF);
            data[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Stereo = false, Data = data };
    }

    public void Play(Id id)
    {
        if (Muted || !_streams.TryGetValue(id, out var stream)) return;
        var p = _players[_next++ % _players.Length];
        p.Stream = stream;
        p.Play();
    }
}
