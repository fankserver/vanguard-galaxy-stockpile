using System;
using BepInEx.Logging;
using VGModAPI;
using VGStockpile.Data;

namespace VGStockpile.Locate;

internal sealed class StationLocator : IStationLocator
{
    private readonly ManualLogSource _log;
    // The captured game owns the map; an ended game refuses rather than focusing a replacement.
    private IGame? _game;
    public StationLocator(ManualLogSource log) { _log = log; }
    internal void BindGame(IGame? game) => _game = game;
    public void Locate(StationStorageSnapshot snapshot) => LocateByGuid(snapshot.StationId);
    public void LocateByGuid(string stationGuid)
    {
        if (string.IsNullOrEmpty(stationGuid) || _game is not { IsActive: true } game) return;
        var status = game.Navigation.FocusPoi(stationGuid);
        if (status != NavigationStatus.Succeeded) _log.LogWarning($"Station focus refused: {status} ({stationGuid}).");
    }
}
