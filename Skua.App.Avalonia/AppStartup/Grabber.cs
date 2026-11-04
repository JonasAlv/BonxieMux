using CommunityToolkit.Mvvm.DependencyInjection;
using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Skua.App.Avalonia.ViewModels.Grabber;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Skua.App.Avalonia.AppStartup;

internal class Grabber
{
    internal static GrabberViewModel CreateViewModel(IServiceProvider s)
    {
        return new GrabberViewModel(s.GetRequiredService<IEnumerable<GrabberListViewModel>>());
    }
    
    internal static IEnumerable<GrabberListViewModel> CreateListViewModels(IServiceProvider s)
    {
        IGrabberService grabberService = s.GetService<IGrabberService>()!;
        List<GrabberTaskViewModel> baseQuestCommands = new()
        {
            new("Open", grabberService.OpenQuests),
            new("Accept", grabberService.AcceptQuests)
        };
        List<GrabberTaskViewModel> questCommands = new(baseQuestCommands)
        {
            new("Register", grabberService.RegisterQuests),
            new("Fake Complete", grabberService.UpdateQuest),
            new("Unregister All", async (i, p, t) =>
            {
                p.Report("Working...");
                await Task.Run(() => Ioc.Default.GetService<IScriptQuest>()!.UnregisterAllQuests(), t);
                p.Report("Finished.");
            })
        };
        List<GrabberTaskViewModel> inventoryCommands = new()
        {
            new("Equip", grabberService.EquipItems),
            new("Sell", grabberService.SellItem) { IsDestructive = true },
            new("Sell All", grabberService.SellAllItems) { IsDestructive = true },
            new("To Bank", grabberService.InvToBank)
        };
        List<GrabberTaskViewModel> mapMonstersCommands = new()
        {
            new("Kill", grabberService.KillMonster),
            new("Teleport To", grabberService.TeleportToMonster)
        };
        List<GrabberTaskViewModel> mapItemCommands = new(baseQuestCommands)
        {
            new("Get Map Item", grabberService.GetMapItem)
        };
        return new List<GrabberListViewModel>()
        {
            // Order and Group drive the grouped sidebar in the Grabber window.
            new("Shop Items", grabberService, GrabberTypes.Shop_Items, new GrabberTaskViewModel("Buy", grabberService.BuyItems), true) { Group = "SHOPS", Icon = MaterialIconKind.StorefrontOutline },
            new("Shop IDs", grabberService, GrabberTypes.Shop_IDs, new GrabberTaskViewModel("Load Shop", grabberService.LoadShop), false) { Group = "SHOPS", Icon = MaterialIconKind.Pound },
            new("Quests", grabberService, GrabberTypes.Quests, questCommands, true) { Group = "QUESTS", Icon = MaterialIconKind.ClipboardTextOutline },
            new("GetMap Item IDs", grabberService, GrabberTypes.GetMap_Item_IDs, mapItemCommands, true) { Group = "QUESTS", Icon = MaterialIconKind.MapMarkerOutline },
            new("Inventory", grabberService, GrabberTypes.Inventory_Items, inventoryCommands, true) { Group = "ITEMS", Icon = MaterialIconKind.BagPersonalOutline },
            new("House Inventory", grabberService, GrabberTypes.House_Inventory_Items, new GrabberTaskViewModel("To Bank", grabberService.HouseInvToBank), true) { Group = "ITEMS", Icon = MaterialIconKind.HomeOutline },
            new("Temp Inventory", grabberService, GrabberTypes.Temp_Inventory_Items, false) { Group = "ITEMS", Icon = MaterialIconKind.TimerSandEmpty },
            new("Bank Items", grabberService, GrabberTypes.Bank_Items, new GrabberTaskViewModel("To Inventory", grabberService.BankToInv), true) { Group = "ITEMS", Icon = MaterialIconKind.BankOutline },
            new("Cell Monsters", grabberService, GrabberTypes.Cell_Monsters, new GrabberTaskViewModel("Kill", grabberService.KillMonster), true) { Group = "MONSTERS", Icon = MaterialIconKind.Target },
            new("Map Monsters", grabberService, GrabberTypes.Map_Monsters, mapMonstersCommands, true) { Group = "MONSTERS", Icon = MaterialIconKind.MapOutline }
        };
    }
}