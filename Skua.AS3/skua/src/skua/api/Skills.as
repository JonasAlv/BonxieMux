package skua.api {

import skua.Main;

public class Skills {

    public function Skills() {
        super();
    }

    private static function actionTimeCheck(skill:*):Boolean {
        var finalCD:int = 0;
        var currentTime:Number = new Date().getTime();
        var hasteMultiplier:Number = 1 - Math.min(Math.max(Main.instance.game.world.myAvatar.dataLeaf.sta.$tha, -1), 0.5);
        if (currentTime - Main.instance.game.world.GCDTS < Main.instance.game.world.GCD) {
            return false;
        }
        if (skill.OldCD != null) {
            finalCD = Math.round(skill.OldCD * hasteMultiplier);
        } else {
            finalCD = Math.round(skill.cd * hasteMultiplier);
        }
        if (currentTime - skill.ts >= finalCD) {
            delete skill.OldCD;
            return true;
        }
        return false;
    }

    public static function canUseSkill(index:int):String {
        var skill:* = Main.instance.game.world.actions.active[index];
        return (Main.instance.game.world.myAvatar.target != null && Main.instance.game.world.myAvatar.target.dataLeaf.intHP > 0 && actionTimeCheck(skill) && skill.isOK && !skill.skillLock && !skill.lock).toString();
    }

    public static function useSkill(index:int):String {
        var skill:* = Main.instance.game.world.actions.active[index];
        if (skill != null && actionTimeCheck(skill)) {
            Main.instance.game.world.testAction(skill);
            return true.toString();
        }

        return false.toString();
    }
}
}
