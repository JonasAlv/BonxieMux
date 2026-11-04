package skua.api {

import skua.Main;

public class Inventory {

    private static function categories():* {
        var domain:* = Main.instance.game.loaderInfo.applicationDomain;
        return domain.hasDefinition("InvCat") ? domain.getDefinition("InvCat") : null;
    }

    public static function hasInventoryCategories():String {
        return (categories() != null).toString();
    }

    // Only the equipped items: serializing the whole inventory takes most of a second in Ruffle.
    public static function equippedItems():String {
        var items:Array = Main.instance.game.world.myAvatar.items;
        var equipped:Array = [];
        for each (var item:* in items) {
            if (item.bEquip) {
                equipped.push(item);
            }
        }
        return JSON.stringify(equipped);
    }

    public static function inventoryBagUsedSlots():int {
        var cat:* = categories();
        var items:Array = Main.instance.game.world.myAvatar.items;
        return cat == null ? items.length : cat.countBag(items);
    }

    public static function inventoryMiscSlots():int {
        var cat:* = categories();
        return cat == null ? 0 : cat.MISC_SLOTS;
    }

    public static function inventoryMiscUsedSlots():int {
        var cat:* = categories();
        return cat == null ? 0 : cat.countMisc(Main.instance.game.world.myAvatar.items);
    }

    public static function isFavoriteItem(itemID:int):String {
        var domain:* = Main.instance.game.loaderInfo.applicationDomain;
        if (domain == null || !domain.hasDefinition("liteAssets.draw.FavStore")) {
            return false.toString();
        }
        var store:* = domain.getDefinition("liteAssets.draw.FavStore");
        return Boolean(store.has(itemID)).toString();
    }

    public static function inventoryPool(itemJson:String):String {
        var cat:* = categories();
        return cat == null ? "bag" : cat.poolOf(JSON.parse(itemJson));
    }

    // Quantity is the amount purchased, rather than the desired final stack.
    public static function inventoryHasSpaceFor(itemJson:String, quantity:int = 1, purchase:Boolean = false, outgoingJson:String = null):String {
        var world:* = Main.instance.game.world;
        var item:Object = JSON.parse(itemJson);
        var cat:* = categories();
        var owned:*;
        if (cat == null) {
            for each (owned in world.myAvatar.items) {
                if (int(owned.ItemID) == int(item.ItemID) && int(owned.iQty) < int(owned.iStk)) return "true";
            }
            return (outgoingJson != null || world.myAvatar.items.length < int(world.myAvatar.objData.iBagSlots)).toString();
        }
        if (cat.isHouse(item)) {
            return (world.myAvatar.houseitems.length < int(world.myAvatar.objData.iHouseSlots)).toString();
        }
        var freed:int = purchase ? cat.freedBy(world.myAvatar.items, item, quantity) : 0;
        if (outgoingJson != null && cat.poolOf(JSON.parse(outgoingJson)) == cat.poolOf(item)) freed++;
        return (!cat.isFullFor(world.myAvatar.items, item, int(world.myAvatar.objData.iBagSlots), freed)).toString();
    }
}
}
