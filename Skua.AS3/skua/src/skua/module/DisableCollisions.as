package skua.module {
public class DisableCollisions extends Module {
    private var _old:*;
    private var _oldR:*;

    public function DisableCollisions() {
        super("DisableCollisions");
    }

    override public function onToggle(game:*):void {
        var world:* = game.world;
        if (enabled) {
            _old = world.arrSolid;
            _oldR = world.arrSolidR;
            world.arrSolid = [];
            world.arrSolidR = [];
        } else {
            world.arrSolid = _old;
            world.arrSolidR = _oldR;
        }
    }

    override public function onFrame(game:*):void {
        onToggle(game);
    }
}

}