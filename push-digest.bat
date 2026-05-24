@echo off
echo === porta-agent-windows-x64: committing digest changes ===
cd /d "C:\Users\hp\porta-agent-windows-x64"

if exist .git\index.lock (
    echo Removing stale git lock...
    del /f .git\index.lock
)

git add src/Porta.Agent.Service/Fleet/DailyDigestReporter.cs ^
        src/Porta.Agent.Service/Fleet/FleetContracts.cs ^
        src/Porta.Agent.Service/Fleet/ControlPlaneClient.cs ^
        src/Porta.Agent.Service/AgentService.cs ^
        src/Porta.Agent.Service/Porta.Agent.Service.csproj ^
        src/Porta.Agent.Core/AgentLocalStoreModels.cs ^
        src/Porta.Agent.Core/IBrainStateStore.cs ^
        src/Porta.Agent.Core/IAgentCoreController.cs ^
        src/Porta.Agent.Core/AgentCoreController.cs ^
        src/Porta.Agent.Core/SqliteBrainStateStore.cs

git commit -m "feat: end-of-day digest reporter v1.2.60"
git push
echo.

echo === porta-control-plane: committing digest changes ===
cd /d "C:\Users\hp\porta-control-plane"

git add package.json ^
        src/core/emailService.ts ^
        src/core/digestEmailBuilder.ts ^
        src/api/controllers/agentController.ts

git commit -m "feat: daily digest endpoint + Resend email delivery"
git push
echo.

echo === Done! ===
pause
