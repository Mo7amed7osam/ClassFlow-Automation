using ZoomAutoAdmit.UIAutomation.WaitingRoom;
using Xunit;

namespace ZoomAutoAdmit.UIAutomation.Tests;

public sealed class WaitingRoomUiaAdmitFlowTests
{
    [Fact]
    public void AdmitVisibleInitially_InvokesRowScopedAdmit()
    {
        var session = new FakeSession().AddWaiting("1", "Mohab Mohamed", "Admit");

        bool result = new WaitingRoomUiaAdmitFlow(_ => { }).TryAdmitWaitingParticipants(session);

        Assert.True(result);
        Assert.Equal(["1:Admit"], session.Invocations);
        Assert.Empty(session.RevealedRows);
    }

    [Fact]
    public void AdmitHiddenUntilHover_RevealsThenInvokes()
    {
        var session = new FakeSession()
            .AddWaiting("1", "Mohab Mohamed")
            .OnReveal("1", "Admit");

        bool result = new WaitingRoomUiaAdmitFlow(_ => { }).TryAdmitWaitingParticipants(session);

        Assert.True(result);
        Assert.Equal(["1"], session.RevealedRows);
        Assert.Equal(["1:Admit"], session.Invocations);
    }

    [Fact]
    public void ViewThenAdmit_UsesSameParticipantRow()
    {
        var session = new FakeSession()
            .AddWaiting("1", "Mohab Mohamed")
            .OnReveal("1", "View")
            .OnInvoke("1", "View", "Admit");

        bool result = new WaitingRoomUiaAdmitFlow(_ => { }).TryAdmitWaitingParticipants(session);

        Assert.True(result);
        Assert.Equal(["1:View", "1:Admit"], session.Invocations);
    }

    [Fact]
    public void MultipleParticipants_AreHandledIndependently()
    {
        var session = new FakeSession()
            .AddWaiting("1", "First Student", "Admit")
            .AddWaiting("2", "Second Student", "Admit");

        bool result = new WaitingRoomUiaAdmitFlow(_ => { }).TryAdmitWaitingParticipants(session);

        Assert.True(result);
        Assert.Equal(["1:Admit", "2:Admit"], session.Invocations);
    }

    [Fact]
    public void JoinedParticipant_IsNeverTouchedByWaitingRoomFlow()
    {
        var session = new FakeSession()
            .AddWaiting("waiting", "Waiting Student", "Admit")
            .AddJoined("joined", "Host Account", "Admit");

        new WaitingRoomUiaAdmitFlow(_ => { }).TryAdmitWaitingParticipants(session);

        Assert.Equal(["waiting:Admit"], session.Invocations);
        Assert.DoesNotContain(session.Invocations, value => value.StartsWith("joined:", StringComparison.Ordinal));
    }

    private sealed class FakeSession : IWaitingRoomUiaSession
    {
        private readonly List<WaitingRoomUiaParticipant> _waiting = [];
        private readonly Dictionary<string, HashSet<string>> _actions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> _revealActions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> _invokeEffects = new(StringComparer.Ordinal);

        public List<string> Invocations { get; } = [];
        public List<string> RevealedRows { get; } = [];

        public FakeSession AddWaiting(string key, string name, params string[] actions)
        {
            _waiting.Add(new WaitingRoomUiaParticipant(key, name));
            _actions[key] = new HashSet<string>(actions, StringComparer.OrdinalIgnoreCase);
            return this;
        }

        public FakeSession AddJoined(string key, string name, params string[] actions)
        {
            _actions[key] = new HashSet<string>(actions, StringComparer.OrdinalIgnoreCase);
            return this;
        }

        public FakeSession OnReveal(string key, params string[] actions)
        {
            _revealActions[key] = actions;
            return this;
        }

        public FakeSession OnInvoke(string key, string action, params string[] actionsAfterInvoke)
        {
            _invokeEffects[$"{key}:{action}"] = actionsAfterInvoke;
            return this;
        }

        public IReadOnlyList<WaitingRoomUiaParticipant> ReadWaitingParticipants() => _waiting.ToArray();

        public bool RevealRowActions(WaitingRoomUiaParticipant participant)
        {
            RevealedRows.Add(participant.Key);
            if (_revealActions.TryGetValue(participant.Key, out var actions))
                foreach (string action in actions) _actions[participant.Key].Add(action);
            return true;
        }

        public bool TryInvokeRowAction(WaitingRoomUiaParticipant participant, string actionName)
        {
            if (!_actions.TryGetValue(participant.Key, out var actions) || !actions.Contains(actionName))
                return false;
            string invocation = $"{participant.Key}:{actionName}";
            Invocations.Add(invocation);
            if (_invokeEffects.TryGetValue(invocation, out var effects))
                foreach (string effect in effects) actions.Add(effect);
            return true;
        }
    }
}
