package skua.module {
import flash.utils.getQualifiedClassName;

public class QuestItemRates extends Module {

    public function QuestItemRates() {
        super("QuestItemRates");
        enabled = true;
    }

    override public function onFrame(game:*):void {
        var modalStack:* = game.ui.ModalStack;
        if (modalStack.numChildren) {
            var cFrame:* = modalStack.getChildAt(0);
            if (getQualifiedClassName(cFrame) == "QFrameMC" && cFrame.cnt.core && cFrame.cnt.core.rewardsRoll) {
                var rewardsRoll:* = cFrame.cnt.core.rewardsRoll;
                var rewardList:* = cFrame.qData.reward;
                for (var i:int = 1; i < rewardsRoll.numChildren; i++) {
                    var rew:* = rewardsRoll.getChildAt(i);
                    if (rew.strType.text.indexOf("%") == -1) {
                        for each (var r:* in rewardList) {
                            if (r.ItemID == rew.ItemID && (!rew.strQ.visible || r.iQty.toString() == rew.strQ.text.substring(1))) {
                                rew.strType.text += " (" + r.iRate + "%)";
                                rew.strType.width = 100;
                                rew.strRate.visible = false;
                            }
                        }
                    }
                }
            }
        }
    }
}
}