package skua.module {
import flash.events.Event;
import flash.utils.getTimer;

import skua.Main;

public class Modules {
    private static var _modules:* = {};

    public static function getModule(name:String):Module {
        return _modules[name];
    }

    public static function registerModule(m:Module):void {
        _modules[m.name] = m;
    }

    public static function enable(name:String):void {
        var module:Module = getModule(name);
        if (module != null) {
            var toggle:Boolean = !module.enabled;
            module.enabled = true;
            if (toggle) {
                module.onToggle(Main.instance.getGame());
            }
        }
    }

    public static function disable(name:String):void {
        var module:Module = getModule(name);
        if (module != null) {
            var toggle:Boolean = module.enabled;
            module.enabled = false;
            if (toggle) {
                module.onToggle(Main.instance.getGame());
            }
        }
    }

    private static const FRAME_UPDATE_INTERVAL:int = 100;
    private static var _lastFrameUpdate:int = 0;

    public static function handleFrame(e:Event):void {
        var now:int = getTimer();
        if (now - _lastFrameUpdate < FRAME_UPDATE_INTERVAL) {
            return;
        }
        _lastFrameUpdate = now;

        var game:* = Main.instance.getGame();
        if (game == null) {
            return;
        }

        for (var name:String in _modules) {
            var module:Module = _modules[name];
            if (module.enabled) {
                module.onFrame(game);
            }
        }
    }

    public static function init():void {
        registerModule(new QuestRequirementWiki());
        registerModule(new QuestItemRates());
        registerModule(new HidePlayers());
        registerModule(new DisableCollisions());
        registerModule(new DisableFX());
        registerModule(new StreamerModeModule());
    }
}
}

import skua.module.Module;

class StreamerModeModule extends Module {
    private var _origName:String = "";
    private var _origGuild:String = "";

    public function StreamerModeModule() {
        super("StreamerMode");
    }

    override public function onToggle(game:*):void {
        if (!game || !game.world) return;
        if (enabled) {
            if (game.world.myAvatar && game.world.myAvatar.objData) {
                var realName:String = game.world.myAvatar.objData.strUsername;
                var currentStreamerName:String = game.streamerName != null ? String(game.streamerName) : "";
                if (realName && realName != "Hidden" && realName != currentStreamerName) {
                    _origName = realName;
                }
                var realGuild:String = game.world.myAvatar.objData.strGuildName;
                if (realGuild && realGuild != "Hidden" && realGuild != currentStreamerName) {
                    _origGuild = realGuild;
                }
            }
            applyStreamer(game);
        } else {
            if (game.world && game.world.myAvatar) {
                if (_origName && _origName.length > 0) {
                    if (game.world.myAvatar.dataLeaf) game.world.myAvatar.dataLeaf.strUsername = _origName;
                    if (game.world.myAvatar.objData) game.world.myAvatar.objData.strUsername = _origName;
                    if (game.world.myAvatar.pMC && game.world.myAvatar.pMC.pname && game.world.myAvatar.pMC.pname.ti)
                        game.world.myAvatar.pMC.pname.ti.text = _origName;
                }
                if (_origGuild && _origGuild.length > 0) {
                    if (game.world.myAvatar.dataLeaf) game.world.myAvatar.dataLeaf.strGuildName = _origGuild;
                    if (game.world.myAvatar.objData) game.world.myAvatar.objData.strGuildName = _origGuild;
                    if (game.world.myAvatar.pMC && game.world.myAvatar.pMC.pname && game.world.myAvatar.pMC.pname.tg)
                        game.world.myAvatar.pMC.pname.tg.text = _origGuild;
                }
            }
            if (game.ui && game.ui.mcInterface) {
                if (game.ui.mcInterface.t1) game.ui.mcInterface.t1.visible = true;
                if (game.ui.mcInterface.te) game.ui.mcInterface.te.visible = true;
                if (game.ui.mcInterface.teGold) game.ui.mcInterface.teGold.visible = true;
                if (game.ui.mcInterface.teCoins) game.ui.mcInterface.teCoins.visible = true;
                if (game.ui.mcInterface.strGold) game.ui.mcInterface.strGold.visible = true;
                if (game.ui.mcInterface.strCoins) game.ui.mcInterface.strCoins.visible = true;
                if (game.ui.mcInterface.txtGold) game.ui.mcInterface.txtGold.visible = true;
                if (game.ui.mcInterface.txtCoins) game.ui.mcInterface.txtCoins.visible = true;
            }
            if (game.updateAreaName != null) {
                try { game.updateAreaName(); } catch(e:*) {}
            }
            if (game.world && game.world.setUserData != null) {
                try { game.world.setUserData(); } catch(e:*) {}
            }
            _origName = "";
            _origGuild = "";
        }
    }

    override public function onFrame(game:*):void {
        if (!enabled) return;
        applyStreamer(game);
    }

    private function applyStreamer(game:*):void {
        if (!game || !game.world) return;

        var nameToDisplay:String = game.streamerName != null ? String(game.streamerName) : "";

        // Avatar names (overhead and data)
        var avt:* = game.world.myAvatar;
        if (avt != null) {
            if (avt.dataLeaf != null) {
                avt.dataLeaf.strUsername = nameToDisplay;
                avt.dataLeaf.strGuildName = nameToDisplay;
            }
            if (avt.objData != null) {
                avt.objData.strUsername = nameToDisplay;
                avt.objData.strGuildName = nameToDisplay;
            }
            if (avt.pMC != null && avt.pMC.pname != null) {
                if (avt.pMC.pname.ti != null)
                    avt.pMC.pname.ti.text = nameToDisplay;
                if (avt.pMC.pname.tg != null)
                    avt.pMC.pname.tg.text = nameToDisplay;
                // Preserve original name and guild colors intact
            }
        }

        // Portrait
        if (game.ui != null) {
            if (game.ui.mcPortrait != null && game.ui.mcPortrait.strName != null)
                game.ui.mcPortrait.strName.text = nameToDisplay;
            if (game.ui.mcPortraitTarget != null && game.ui.mcPortraitTarget.strName != null) {
                if (avt != null && avt.target == avt) {
                    game.ui.mcPortraitTarget.strName.text = nameToDisplay;
                }
            }

            // Interface elements (gold, coins, chat)
            if (game.ui.mcInterface != null) {
                if (game.ui.mcInterface.teGold != null) game.ui.mcInterface.teGold.visible = false;
                if (game.ui.mcInterface.teCoins != null) game.ui.mcInterface.teCoins.visible = false;
                if (game.ui.mcInterface.strGold != null) game.ui.mcInterface.strGold.visible = false;
                if (game.ui.mcInterface.strCoins != null) game.ui.mcInterface.strCoins.visible = false;
                if (game.ui.mcInterface.txtGold != null) game.ui.mcInterface.txtGold.visible = false;
                if (game.ui.mcInterface.txtCoins != null) game.ui.mcInterface.txtCoins.visible = false;
                if (game.ui.mcInterface.t1 != null) game.ui.mcInterface.t1.visible = false;
                if (game.ui.mcInterface.te != null) game.ui.mcInterface.te.visible = false;

                // Static map name in bottom-right area indicator
                if (game.ui.mcInterface.areaList != null && game.ui.mcInterface.areaList.title != null && game.ui.mcInterface.areaList.title.t1 != null) {
                    var rawMap:String = game.world.strMapName != null ? String(game.world.strMapName) : "";
                    if (rawMap.length > 0 && rawMap != "null") {
                        var cleanMap:String = rawMap.charAt(0).toUpperCase() + rawMap.substr(1).toLowerCase();
                        game.ui.mcInterface.areaList.title.t1.text = cleanMap;
                    }
                }
            }
        }
    }
}
