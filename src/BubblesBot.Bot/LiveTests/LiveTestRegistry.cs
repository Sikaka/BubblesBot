namespace BubblesBot.Bot.LiveTests;

public static class LiveTestRegistry
{
    private static readonly IReadOnlyList<ILiveTestCase> Registered =
    [
        new StateInspectionLiveTest(),
        new MapLandmarkInspectLiveTest(),
        new MapDoorInspectLiveTest(),
        new MapDoorOpenLiveTest(),
        new PassiveTreeInspectLiveTest(),
        new PassiveAllocateLiveTest(),
        new InventoryRoundTripLiveTest(),
        new EquipmentSlotsInspectLiveTest(),
        new SocketLayoutInspectLiveTest(),
        new ItemSocketIdentityLiveTest(),
        new EquipmentWeaponRoundTripLiveTest(),
        new GemSocketRoundTripLiveTest(),
        new GemLevelUiInspectLiveTest(),
        new GemLevelUiHoverLiveTest(),
        new GemLevelUpSingleLiveTest(),
        new GemLevelUpFinalRowLiveTest(),
        new HeldWeaponRecoveryLiveTest(),
        new CursorPickupRoundTripLiveTest(),
        new StashCtrlClickRoundTripLiveTest(),
        new MapStashTierHoverLiveTest(),
        new MapStashSelectorRoundTripLiveTest(),
        new StashActiveTabDiscoveryLiveTest(),
        new StashSwitchToTargetLiveTest(),
        new AtlasNodeHoverDiscoveryLiveTest(),
        new GuardianKiracInvitationOpenLiveTest(),
        new GuardianInvitationInspectLiveTest(),
        new GuardianInvitationRollLiveTest(),
        new GuardianMapInventoryInspectLiveTest(),
        new GuardianMapIdentifyLiveTest(),
        new GuardianMapRollLiveTest(),
        new GuardianFormedEncounterMonitorLiveTest(),
        new GuardianMapEncounterMonitorLiveTest(),
        new GuardianFormedPodiumInspectLiveTest(),
        new GuardianAtlasCalibrationLiveTest(),
        new SellWindowInspectLiveTest(),
        new SellOfferCtrlClickRoundTripLiveTest(),
        new SellCancelRoundTripLiveTest(),
        new VendorHoverRoundTripLiveTest(),
        new VendorPageRoundTripLiveTest(),
        new VendorVisibleOfferSweepLiveTest(),
        new VendorPageOneOfferSweepLiveTest(),
        new VendorUnavailableOfferLiveTest(),
        new VendorAffordableOfferLiveTest(),
        new NpcShopRoundTripLiveTest(),
        new BestelDialogShapeLiveTest(),
        new EnemyAtGateRewardDiscoveryLiveTest(),
        new QuestRewardDialogCatalogLiveTest(),
        new QuestRewardOptionDiscoveryLiveTest(),
        new RouteRewardPairClaimLiveTest(),
        new EnemyAtGateRewardClaimLiveTest(),
        new VisibleUiTextInspectLiveTest(),
        new UiHoverInspectLiveTest(),
        new VisibleUiBranchInspectLiveTest(),
        new SystemMenuStructureToggleLiveTest(),
        new LogoutToCharacterSelectLiveTest(),
        new CharacterSelectInspectLiveTest(),
        new CharacterSelectPlayRecoveryLiveTest(),
        new LogoutReentryRoundTripLiveTest(),
        new EscapeMenuRoundTripLiveTest(),
    ];

    static LiveTestRegistry()
    {
        var duplicate = Registered
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"duplicate live-test ID '{duplicate.Key}'");
    }

    public static IReadOnlyList<ILiveTestCase> All => Registered;

    public static ILiveTestCase? Find(string id)
        => Registered.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

    public static void PrintCatalog(TextWriter writer)
    {
        writer.WriteLine("Available Bot live tests:");
        foreach (var test in Registered.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            writer.WriteLine($"  {test.Id,-24} {test.Mutation,-12} {(test.DrivesInput ? "INPUT" : "READ")}  {test.Name}");
            writer.WriteLine($"    {test.Description}");
            writer.WriteLine($"    Setup: {test.ManualSetup}");
        }
        writer.WriteLine();
        writer.WriteLine("Run:");
        writer.WriteLine("  BubblesBot.Bot --live-test <id> --phase <research|single|repeatable> [safety arguments]");
        writer.WriteLine("Input-driving tests additionally require --arm --confirm-setup --expect-character <name> --expect-area-hash <hash>.");
        writer.WriteLine("Repeatable phase additionally requires --iterations <3..20>.");
        writer.WriteLine("Economic/irreversible tests additionally require --commit in every phase.");
        writer.WriteLine("Reward-claim tests additionally require --expect-reward \"<exact base name>\".");
    }

    public static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  BubblesBot.Bot --list-live-tests");
        writer.WriteLine("  BubblesBot.Bot --live-test <id> --phase <research|single|repeatable> [--iterations <3..20>] [safety arguments]");
    }
}
