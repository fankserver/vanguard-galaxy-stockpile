using System;
using BepInEx.Logging;
using VGModAPI;
using VGStockpile.Data;

namespace VGStockpile.Locate;

internal sealed class StationLocator : IStationLocator
{
    private readonly ManualLogSource _log;
    private Guid? _session;
    public StationLocator(ManualLogSource log) { _log = log; }
    internal void BindSession(Guid? session) => _session = session;
    public void Locate(StationStorageSnapshot snapshot) => LocateByGuid(snapshot.StationId);
    public void LocateByGuid(string stationGuid)
    {
        if (string.IsNullOrEmpty(stationGuid) || _session is not Guid session) return;
        var status = ModApi.Services.Navigation.FocusPoi(session, stationGuid);
        if (status != NavigationStatus.Succeeded) _log.LogWarning($"Station focus refused: {status} ({stationGuid}).");
    }
}
