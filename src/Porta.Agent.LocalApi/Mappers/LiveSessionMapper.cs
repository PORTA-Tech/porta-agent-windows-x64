using Porta.Agent.Core;

namespace Porta.Agent.LocalApi.Mappers;

internal static class LiveSessionMapper
{
    public static LocalApiLiveSessionStateResponse Map(AgentLiveSessionState state)
    {
        return new LocalApiLiveSessionStateResponse(
            UpdatedAtUtc: state.UpdatedAtUtc,
            SessionStartedAtUtc: state.SessionStartedAtUtc,
            LastInputEventUtc: state.LastInputEventUtc,
            AgentInstanceId: state.AgentInstanceId,
            SessionId: state.SessionId,
            KeyboardActive: state.KeyboardActive,
            MouseActive: state.MouseActive,
            UserActive: state.UserActive,
            Idle: state.Idle,
            IdleSeconds: state.IdleSeconds,
            FocusAppHash: state.FocusAppHash,
            Freshness: state.Freshness,
            DegradedReason: state.DegradedReason,
            FocusStartedAtUtc: state.FocusStartedAtUtc);
    }
}
