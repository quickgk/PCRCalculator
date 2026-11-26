using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Elements.Battle
{
    public sealed class ZeroPressPolicyData
    {
        public ZeroPressPolicyData Copy() => new() { OpeningAuto = OpeningAuto, OpeningMask = OpeningMask,
            Rules = Rules?.Select(r => r == null ? null : new ZeroPressRule { UnitId = r.UnitId,
                Occurrence = r.Occurrence, Auto = r.Auto, Mask = r.Mask }).ToList() };
        [JsonRequired] public bool OpeningAuto { get; set; }
        [JsonRequired] public int OpeningMask { get; set; }
        [JsonRequired] public List<ZeroPressRule> Rules { get; set; }
    }

    public sealed class ZeroPressRule
    {
        [JsonRequired] public int UnitId { get; set; }
        [JsonRequired] public int Occurrence { get; set; }
        [JsonRequired] public bool Auto { get; set; }
        [JsonRequired] public int Mask { get; set; }
    }

    public sealed record ZeroPressState(bool Auto, int Mask);
    public sealed record ZeroPressEvent(int UnitId, int Occurrence, int Frame, bool Auto, int Mask, bool Applied);

    // One object per StartByServer, NEVER shared across seeds/workers. There is no
    // clock, scheduler, UB button, enemy list, RNG or TP information in this API.
    internal sealed class ZeroPressRun<T> where T : class
    {
        private readonly int[] ids;
        private readonly int[] counts;
        private readonly Dictionary<(int, int), ZeroPressState> rules = new();
        private readonly Dictionary<T, int> slots = new(ReferenceEqualityComparer.Instance);
        private readonly List<ZeroPressEvent> events = new();
        private readonly ZeroPressState requestedOpening;
        private readonly Action<bool> setAuto;
        private readonly Action<T, bool> setPressed;
        private T[] players;
        private ZeroPressState state;
        public ZeroPressState Opening { get; private set; }

        public ZeroPressRun(ZeroPressPolicyData policy, IEnumerable<int> requestOrder,
            Action<bool> setAuto, Action<T, bool> setPressed)
        {
            ids = requestOrder.ToArray();
            if (ids.Length < 1 || ids.Length > 5 || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length)
                throw new ArgumentException("ZeroPressPolicy requires 1..5 distinct player unit IDs.");
            counts = new int[ids.Length];
            requestedOpening = ValidState(policy.OpeningAuto, policy.OpeningMask);
            if (policy.Rules == null) throw new ArgumentException("ZeroPressPolicy.Rules must be an array.");
            foreach (var r in policy.Rules)
            {
                if (r == null || (r.UnitId != 0 && Array.IndexOf(ids, r.UnitId) < 0) || r.Occurrence < 1)
                    throw new ArgumentException("ZeroPressPolicy rule requires a player UnitId (or 0 for primary boss) and a positive Occurrence.");
                if (!rules.TryAdd((r.UnitId, r.Occurrence), ValidState(r.Auto, r.Mask)))
                    throw new ArgumentException("Duplicate ZeroPressPolicy rule for the same player UB.");
            }
            this.setAuto = setAuto;
            this.setPressed = setPressed;
        }

        private static ZeroPressState ValidState(bool auto, int mask) => (uint)mask <= 31
            ? new ZeroPressState(auto, mask) : throw new ArgumentException("ZeroPressPolicy mask must be 0..31.");

        // Bind ORIGINAL player objects by requested ID, not the engine's sorted position.
        // Additional spawns, even a same-ID clone, cannot steal a slot/counter.
        public void Open(IReadOnlyDictionary<int, T> roster)
        {
            if (Opening != null) throw new InvalidOperationException("ZeroPressPolicy opening already applied.");
            players = new T[ids.Length];
            for (var i = 0; i < ids.Length; i++)
            {
                if (!roster.TryGetValue(ids[i], out var p) || p is null || !slots.TryAdd(p, i))
                    throw new InvalidOperationException("Cannot resolve original ZeroPressPolicy player in battle.");
                players[i] = p;
            }
            Apply(requestedOpening);
            Opening = state;
        }

        private void Apply(ZeroPressState next)
        {
            setAuto(next.Auto);
            for (var i = 0; i < players.Length; i++) setPressed(players[i], (next.Mask & (1 << i)) != 0);
            state = next;
        }

        // Called synchronously AFTER the native UB has paused the clock. Frame is
        // only copied into the trace, never examined to choose or schedule a rule.
        private int bossCount;
        public const float BossMinBlackoutSeconds = 1.0f;
        public void OnPlayerCutIn(T actor, int frame)
        {
            if (!slots.TryGetValue(actor, out var i)) return;
            Record(ids[i], ++counts[i], frame);
        }

        public void OnBossBlackout(float blackoutSeconds, int frame)
        {
            // Conservative input convention: at normal speed allow a full second for
            // panel changes. Do not turn a one-frame native synchronization into a rule.
            if (!(blackoutSeconds >= BossMinBlackoutSeconds) || Opening == null) return;
            Record(0, ++bossCount, frame);
        }

        private void Record(int id, int n, int frame)
        {
            var matched = rules.TryGetValue((id, n), out var next);
            if (matched) Apply(next);
            events.Add(new ZeroPressEvent(id, n, frame, state.Auto, state.Mask, matched));
        }

        public List<ZeroPressEvent> TraceSnapshot() => Opening != null
            ? new List<ZeroPressEvent>(events) : throw new InvalidOperationException("ZeroPressPolicy opening never applied.");
    }
}
