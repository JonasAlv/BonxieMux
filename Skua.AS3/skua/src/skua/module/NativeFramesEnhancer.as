package skua.module {
import flash.display.Stage;
import flash.events.Event;
import flash.filters.BitmapFilterQuality;
import flash.filters.DropShadowFilter;
import flash.filters.GlowFilter;
import flash.geom.ColorTransform;
import flash.text.TextField;
import flash.text.TextFieldAutoSize;
import flash.text.TextFormat;
import flash.text.TextFormatAlign;

import skua.Main;

/**
 * Ported subset of NativeFramesEnhancer.hx:
 * 1) UnitFrameHealth: curHP / maxHP on the left + live % on the right on player & target frames.
 * 2) MovableTargetFrame: draggable target portrait frame with anchored close button & persistence.
 */
public class NativeFramesEnhancer extends Module {
    private static var _instance:NativeFramesEnhancer = null;

    public static var healthTextEnabled:Boolean = true;
    public static var movableTargetEnabled:Boolean = true;

    private static var _hookedStage:Stage = null;
    private static var _playerBarWidth:Number = 0;
    private static var _targetBarWidth:Number = 0;
    private static var _targetDrag:NativeFrameDrag = null;

    // Crisp 1px black outline + subtle drop shadow matching ApiStyle.getTextOutlineFilter()
    private static var TEXT_OUTLINE_FILTERS:Array = [
        new GlowFilter(0x000000, 1.0, 2.0, 2.0, 8.0, BitmapFilterQuality.MEDIUM),
        new DropShadowFilter(1.0, 45.0, 0x000000, 1.0, 1.0, 1.0, 4.0, BitmapFilterQuality.MEDIUM)
    ];

    public function NativeFramesEnhancer() {
        super("NativeFramesEnhancer");
        _instance = this;
        enabled = true;
        try {
            if (Main.instance != null && Main.instance.external != null) {
                Main.instance.external.addCallback("setUnitFrameHealth", setHealthTextEnabled);
                Main.instance.external.addCallback("setMovableTargetFrame", setMovableTargetEnabled);
            }
        } catch (err:Error) {}
    }

    public static function isMovableTargetEnabled():Boolean {
        return (_instance == null || _instance.enabled) && movableTargetEnabled;
    }

    public static function setHealthTextEnabled(enable:*):void {
        var next:Boolean = parseBool(enable);
        healthTextEnabled = next;
        var game:* = (Main.instance != null) ? Main.instance.getGame() : null;
        if (!next || (_instance != null && !_instance.enabled)) {
            restoreAllHealthFrames(game);
        } else {
            updateFrames(game);
        }
    }

    public static function setMovableTargetEnabled(enable:*):void {
        var next:Boolean = parseBool(enable);
        movableTargetEnabled = next;
        var game:* = (Main.instance != null) ? Main.instance.getGame() : null;
        if (!next || (_instance != null && !_instance.enabled)) {
            if (_targetDrag != null) {
                _targetDrag.disableAndReset(game);
            } else {
                NativeFrameDrag.resetNativeTargetPosition(game);
            }
        } else {
            if (game != null && game.ui != null && game.ui.mcPortraitTarget != null) {
                ensureTargetDrag(game.ui.mcPortraitTarget);
            }
            if (_targetDrag != null) {
                _targetDrag.restorePosition();
            }
            updateFrames(game);
        }
    }

    override public function onToggle(game:*):void {
        if (!enabled) {
            restoreAllHealthFrames(game);
            if (_targetDrag != null) {
                _targetDrag.disableAndReset(game);
            } else {
                NativeFrameDrag.resetNativeTargetPosition(game);
            }
        } else {
            if (movableTargetEnabled && game != null && game.ui != null && game.ui.mcPortraitTarget != null) {
                ensureTargetDrag(game.ui.mcPortraitTarget);
                if (_targetDrag != null) {
                    _targetDrag.restorePosition();
                }
            }
            updateFrames(game);
        }
    }

    override public function onFrame(game:*):void {
        if (!enabled) {
            return;
        }
        ensureStageHook(game);
        updateFrames(game);
    }

    private static function parseBool(v:*):Boolean {
        return (v === true || v === 1 || String(v).toLowerCase() == "true");
    }

    private static function ensureStageHook(game:*):void {
        var st:Stage = null;
        try {
            if (game != null && game.stage != null) {
                st = game.stage as Stage;
            }
        } catch (err:Error) {}
        if (st == null || _hookedStage == st) {
            return;
        }
        if (_hookedStage != null) {
            try {
                _hookedStage.removeEventListener(Event.ENTER_FRAME, onStageEnterFrame);
            } catch (remErr:Error) {}
        }
        _hookedStage = st;
        try {
            st.addEventListener(Event.ENTER_FRAME, onStageEnterFrame, false, 0, true);
        } catch (addErr:Error) {}
    }

    private static function onStageEnterFrame(e:Event):void {
        if (_instance == null || !_instance.enabled) {
            return;
        }
        if (!healthTextEnabled && !movableTargetEnabled) {
            return;
        }
        var game:* = (Main.instance != null) ? Main.instance.getGame() : null;
        updateFrames(game);
    }

    private static function updateFrames(game:*):void {
        if (_instance != null && !_instance.enabled) {
            return;
        }
        if (game == null || game.ui == null) {
            return;
        }
        try {
            if ("mcLogin" in game && game.mcLogin != null && game.mcLogin.visible) {
                return;
            }
        } catch (loginErr:Error) {}

        ensureStageHook(game);

        if (isMovableTargetEnabled()) {
            try {
                var targetMc:* = game.ui.mcPortraitTarget;
                if (targetMc != null) {
                    ensureTargetDrag(targetMc);
                }
            } catch (dragErr:Error) {}
        } else if (_targetDrag != null) {
            _targetDrag.ensureDetached();
        }

        updatePlayerFrame(game);
        updateTargetFrame(game);
    }

    private static function ensureTargetDrag(targetMc:*):void {
        if (_targetDrag == null) {
            _targetDrag = new NativeFrameDrag(targetMc, "posTargetFrame");
        } else if (_targetDrag.isStale(targetMc)) {
            _targetDrag.rebind(targetMc);
        } else {
            _targetDrag.ensureAttached();
        }
    }

    private static function updatePlayerFrame(game:*):void {
        if (game == null || game.ui == null || game.ui.mcPortrait == null) return;
        var mcPortrait:* = game.ui.mcPortrait;
        if (!mcPortrait.visible) return;

        var hpClip:* = mcPortrait.HP;
        if (hpClip == null) return;

        var strIntHP:TextField = hpClip.strIntHP as TextField;
        if (strIntHP == null) return;

        if (!healthTextEnabled) {
            restoreNativeFrame(hpClip);
            return;
        }

        if (hpClip.intHPbar != null && hpClip.intHPbar.transform != null) {
            hpClip.intHPbar.transform.colorTransform = new ColorTransform();
        }

        strIntHP.visible = false;

        if (_playerBarWidth <= 0) {
            _playerBarWidth = measureBarWidth(hpClip, strIntHP);
        }

        var leaf:* = (game.world != null && game.world.myAvatar != null) ? game.world.myAvatar.dataLeaf : null;
        if (leaf == null) return;

        var hp:int = (leaf.intHP != null) ? int(leaf.intHP) : 0;
        var maxHp:int = (leaf.intHPMax != null) ? int(leaf.intHPMax) : 0;
        var shield:int = (leaf.intSG != null) ? int(leaf.intSG) : 0;
        if (maxHp <= 0) maxHp = 1;

        renderEnhancedHP(hpClip, strIntHP, _playerBarWidth, hp, maxHp, shield);
    }

    private static function updateTargetFrame(game:*):void {
        if (game == null || game.ui == null || game.ui.mcPortraitTarget == null) return;
        var mcPortraitTarget:* = game.ui.mcPortraitTarget;
        if (!mcPortraitTarget.visible) return;

        var targetAvt:* = (game.world != null && game.world.myAvatar != null) ? game.world.myAvatar.target : null;
        if (targetAvt == null) return;

        var hpClip:* = mcPortraitTarget.HP;
        if (hpClip == null) return;

        var strIntHP:TextField = hpClip.strIntHP as TextField;
        if (strIntHP == null) return;

        if (!healthTextEnabled) {
            restoreNativeFrame(hpClip);
            return;
        }

        if (hpClip.intHPbar != null && hpClip.intHPbar.transform != null) {
            hpClip.intHPbar.transform.colorTransform = new ColorTransform();
        }

        strIntHP.visible = false;

        if (_targetBarWidth <= 0) {
            _targetBarWidth = measureBarWidth(hpClip, strIntHP);
        }

        var leaf:* = targetAvt.dataLeaf;
        if (leaf == null) return;

        var hp:int = (leaf.intHP != null) ? int(leaf.intHP) : 0;
        var maxHp:int = (leaf.intHPMax != null) ? int(leaf.intHPMax) : 0;
        var shield:int = (leaf.intSG != null) ? int(leaf.intSG) : 0;
        if (maxHp <= 0) maxHp = 1;

        renderEnhancedHP(hpClip, strIntHP, _targetBarWidth, hp, maxHp, shield);

        if (_targetDrag != null && isMovableTargetEnabled()) {
            _targetDrag.syncSatellites();
        }
    }

    private static function measureBarWidth(hpClip:*, strIntHP:TextField):Number {
        try {
            if (hpClip.intHPbar != null) {
                var sx:Number = Math.abs(hpClip.intHPbar.scaleX);
                var rawW:Number = Math.abs(hpClip.intHPbar.width);
                var fullW:Number = (sx > 0.01) ? (rawW / sx) : rawW;
                if (fullW > 60) {
                    return fullW;
                }
            }
            if (strIntHP != null && strIntHP.width > 60) {
                return strIntHP.width;
            }
        } catch (err:Error) {}
        return 122.0;
    }

    private static function renderEnhancedHP(hpClip:*, strIntHP:TextField, barW:Number, hp:int, maxHp:int, shield:int):void {
        var rightReserve:Number = 34.0;

        var txtEnhancedHP:TextField = hpClip.getChildByName("_txtEnhancedHP") as TextField;
        if (txtEnhancedHP == null) {
            txtEnhancedHP = createOverlayField("_txtEnhancedHP", strIntHP, TextFormatAlign.LEFT);
            hpClip.addChild(txtEnhancedHP);
        }

        var txtEnhancedPct:TextField = hpClip.getChildByName("_txtEnhancedPct") as TextField;
        if (txtEnhancedPct == null) {
            txtEnhancedPct = createOverlayField("_txtEnhancedPct", strIntHP, TextFormatAlign.RIGHT);
            hpClip.addChild(txtEnhancedPct);
        }

        var fieldH:Number = (strIntHP.height > 0) ? strIntHP.height : 16.0;

        txtEnhancedHP.visible = true;
        txtEnhancedHP.x = 3.0;
        txtEnhancedHP.y = strIntHP.y;
        txtEnhancedHP.width = barW - rightReserve - 5.0;
        txtEnhancedHP.height = fieldH;

        txtEnhancedPct.visible = true;
        txtEnhancedPct.x = barW - 35.0;
        txtEnhancedPct.y = strIntHP.y;
        txtEnhancedPct.width = 33.0;
        txtEnhancedPct.height = fieldH;

        var hpStr:String;
        if (hp <= 0) {
            hpStr = "0 / " + formatNumber(maxHp);
        } else {
            var shieldStr:String = (shield > 0) ? (" +" + formatNumber(shield)) : "";
            hpStr = formatNumber(hp) + " / " + formatNumber(maxHp) + shieldStr;
        }
        if (txtEnhancedHP.text != hpStr) {
            txtEnhancedHP.text = hpStr;
        }

        var hpRatio:Number = Math.max(0.0, Math.min(1.0, hp / maxHp));
        var pctVal:int = (hp > 0 && hpRatio < 0.01) ? 1 : Math.round(hpRatio * 100);
        var pctStr:String = pctVal + "%";
        if (txtEnhancedPct.text != pctStr) {
            txtEnhancedPct.text = pctStr;
        }
    }

    private static function createOverlayField(name:String, template:TextField, align:String):TextField {
        var field:TextField = new TextField();
        field.name = name;
        field.selectable = false;
        field.mouseEnabled = false;
        field.autoSize = TextFieldAutoSize.NONE;

        var srcFmt:TextFormat = (template != null) ? template.defaultTextFormat : null;
        var fontName:String = (srcFmt != null && srcFmt.font != null && srcFmt.font != "") ? srcFmt.font : "_sans";
        var fontSize:* = (srcFmt != null && srcFmt.size != null) ? srcFmt.size : 10;

        var fmt:TextFormat = new TextFormat(fontName, fontSize, 0xFFFFFF, true);
        fmt.align = align;
        field.defaultTextFormat = fmt;
        field.setTextFormat(fmt);
        field.filters = TEXT_OUTLINE_FILTERS;
        return field;
    }

    private static function restoreAllHealthFrames(game:*):void {
        if (game == null || game.ui == null) return;
        try {
            if (game.ui.mcPortrait != null && game.ui.mcPortrait.HP != null) {
                restoreNativeFrame(game.ui.mcPortrait.HP);
            }
        } catch (err:Error) {}
        try {
            if (game.ui.mcPortraitTarget != null && game.ui.mcPortraitTarget.HP != null) {
                restoreNativeFrame(game.ui.mcPortraitTarget.HP);
            }
        } catch (err2:Error) {}
    }

    private static function restoreNativeFrame(hpClip:*):void {
        if (hpClip == null) return;
        try {
            if (hpClip.strIntHP != null) {
                hpClip.strIntHP.visible = true;
            }
            var oldHP:* = hpClip.getChildByName("_txtEnhancedHP");
            if (oldHP != null) oldHP.visible = false;
            var oldPct:* = hpClip.getChildByName("_txtEnhancedPct");
            if (oldPct != null) oldPct.visible = false;
            if (hpClip.intHPbar != null && hpClip.intHPbar.transform != null) {
                hpClip.intHPbar.transform.colorTransform = new ColorTransform();
            }
        } catch (err:Error) {}
    }

    private static function formatNumber(n:int):String {
        if (n < 0) return "0";
        if (n >= 10000000) {
            return String(Math.round(n / 1000000)) + "M";
        }
        if (n >= 1000000) {
            var m:Number = Math.round(n / 100000) / 10.0;
            return m + "M";
        }
        if (n >= 100000) {
            var k:Number = Math.round(n / 1000);
            return k + "k";
        }
        var s:String = String(n);
        var len:int = s.length;
        if (len <= 3) return s;
        var res:String = "";
        for (var i:int = 0; i < len; i++) {
            if (i > 0 && (len - i) % 3 == 0) res += ",";
            res += s.charAt(i);
        }
        return res;
    }
}
}

import flash.display.Shape;
import flash.display.Sprite;
import flash.display.Stage;
import flash.events.Event;
import flash.events.MouseEvent;
import flash.net.SharedObject;
import flash.utils.setTimeout;

import skua.Main;
import skua.module.NativeFramesEnhancer;

/**
 * Draggable controller for native target unit frame (mcPortraitTarget) and its close button.
 */
class NativeFrameDrag {
    private static const THRESHOLD_SQ:Number = 9.0; // 3px drag threshold

    private static const FRAME_OFFSET_X:Number = 14.0;
    private static const FRAME_OFFSET_Y:Number = 12.0;
    private static const FRAME_WIDTH:Number    = 188.0;
    private static const FRAME_HEIGHT:Number   = 81.0;

    private static const DEFAULT_TARGET_X:Number = 235.0;
    private static const DEFAULT_TARGET_Y:Number = 2.0;
    private static const CLOSE_OFFSET_X:Number   = 178.7; // 413.7 - 235.0
    private static const CLOSE_OFFSET_Y:Number   = 8.0;   // 10.0 - 2.0

    private static const SO_NAME:String = "SkuaNativeFrames";

    private var _target:* = null;
    private var _litePrefKey:String;
    private var _defaultX:Number = DEFAULT_TARGET_X;
    private var _defaultY:Number = DEFAULT_TARGET_Y;
    private var _defaultRecorded:Boolean = false;
    private var _hasSavedCache:Boolean = false;
    private var _savedLookupDone:Boolean = false;
    private var _savedX:Number = 0;
    private var _savedY:Number = 0;

    private var _closeOffsetX:Number = CLOSE_OFFSET_X;
    private var _closeOffsetY:Number = CLOSE_OFFSET_Y;

    private var _stage:Stage = null;
    private var _isDragging:Boolean = false;
    private var _hasDragged:Boolean = false;
    private var _startX:Number = 0;
    private var _startY:Number = 0;
    private var _origX:Number = 0;
    private var _origY:Number = 0;

    private var _backdrop:Sprite = null;
    private var _bgShape:Shape = null;
    private var _hitLayer:Sprite = null;

    public function NativeFrameDrag(target:*, litePrefKey:String) {
        _litePrefKey = litePrefKey;
        rebind(target);
    }

    public static function resetNativeTargetPosition(game:*):void {
        try {
            if (game != null && game.ui != null) {
                if (game.ui.mcPortraitTarget != null) {
                    game.ui.mcPortraitTarget.x = DEFAULT_TARGET_X;
                    game.ui.mcPortraitTarget.y = DEFAULT_TARGET_Y;
                }
                if (game.ui.btnTargetPortraitClose != null) {
                    game.ui.btnTargetPortraitClose.x = DEFAULT_TARGET_X + CLOSE_OFFSET_X;
                    game.ui.btnTargetPortraitClose.y = DEFAULT_TARGET_Y + CLOSE_OFFSET_Y;
                }
            }
        } catch (err:Error) {}
    }

    public function isStale(current:*):Boolean {
        if (current == null) return false;
        if (_target != current) return true;
        if (_target == null) return true;
        if (_target.parent == null && current.parent != null) return true;
        return false;
    }

    public function rebind(newTarget:*):void {
        if (_target == newTarget && _target != null && _target.parent != null) return;

        destroy();
        _target = newTarget;
        if (_target == null) return;

        _target.mouseEnabled = true;
        ensureHitLayer();

        _target.addEventListener(MouseEvent.MOUSE_DOWN, onDown, false);
        _target.addEventListener(MouseEvent.MOUSE_DOWN, onDown, true);
        _target.addEventListener(MouseEvent.ROLL_OVER, onRollOver, false);
        _target.addEventListener(MouseEvent.ROLL_OUT, onRollOut, false);
        _target.addEventListener(MouseEvent.CLICK, onClickCapture, true);

        _defaultX = DEFAULT_TARGET_X;
        _defaultY = DEFAULT_TARGET_Y;
        _closeOffsetX = CLOSE_OFFSET_X;
        _closeOffsetY = CLOSE_OFFSET_Y;
        _defaultRecorded = false;
        ensureAttached();
    }

    public function destroy():void {
        stopPress();
        hideBackdrop();
        if (_target != null) {
            try {
                _target.removeEventListener(MouseEvent.MOUSE_DOWN, onDown, false);
                _target.removeEventListener(MouseEvent.MOUSE_DOWN, onDown, true);
                _target.removeEventListener(MouseEvent.ROLL_OVER, onRollOver, false);
                _target.removeEventListener(MouseEvent.ROLL_OUT, onRollOut, false);
                _target.removeEventListener(MouseEvent.CLICK, onClickCapture, true);
            } catch (err:Error) {}
            if (_hitLayer != null && _hitLayer.parent == _target) {
                try { _target.removeChild(_hitLayer); } catch (remErr:Error) {}
            }
        }
        if (_hitLayer != null) {
            _hitLayer.removeEventListener(MouseEvent.MOUSE_DOWN, onDown, false);
            _hitLayer = null;
        }
        _target = null;
    }

    public function ensureDetached():void {
        stopPress();
        hideBackdrop();
        if (_hitLayer != null && _hitLayer.parent != null) {
            try { _hitLayer.parent.removeChild(_hitLayer); } catch (err:Error) {}
        }
    }

    public function disableAndReset(game:*):void {
        ensureDetached();
        try {
            if (_target != null) {
                _target.x = _defaultX;
                _target.y = _defaultY;
            }
            var g:* = (game != null) ? game : ((Main.instance != null) ? Main.instance.getGame() : null);
            if (g != null && g.ui != null) {
                if (g.ui.mcPortraitTarget != null) {
                    g.ui.mcPortraitTarget.x = _defaultX;
                    g.ui.mcPortraitTarget.y = _defaultY;
                }
                if (g.ui.btnTargetPortraitClose != null) {
                    g.ui.btnTargetPortraitClose.x = _defaultX + _closeOffsetX;
                    g.ui.btnTargetPortraitClose.y = _defaultY + _closeOffsetY;
                }
            }
        } catch (err:Error) {}
    }

    private function ensureHitLayer():void {
        if (_target == null) return;
        if (!NativeFramesEnhancer.isMovableTargetEnabled()) {
            if (_hitLayer != null && _hitLayer.parent != null) {
                try { _hitLayer.parent.removeChild(_hitLayer); } catch (err:Error) {}
            }
            return;
        }
        if (_hitLayer == null) {
            _hitLayer = new Sprite();
            _hitLayer.name = "_dragHitLayer";
            _hitLayer.mouseEnabled = true;
            _hitLayer.mouseChildren = false;
            _hitLayer.buttonMode = false;
            _hitLayer.graphics.beginFill(0x000000, 0.01);
            _hitLayer.graphics.drawRect(FRAME_OFFSET_X, FRAME_OFFSET_Y, FRAME_WIDTH, FRAME_HEIGHT);
            _hitLayer.graphics.endFill();
            _hitLayer.addEventListener(MouseEvent.MOUSE_DOWN, onDown, false);
        }
        if (_hitLayer.parent != _target) {
            try {
                _target.addChildAt(_hitLayer, 0);
            } catch (addErr:Error) {
                try { _target.addChild(_hitLayer); } catch (addErr2:Error) {}
            }
        }
    }

    public function ensureAttached():void {
        if (_target == null) return;
        ensureHitLayer();

        if (!NativeFramesEnhancer.isMovableTargetEnabled()) {
            return;
        }

        var g:* = (Main.instance != null) ? Main.instance.getGame() : null;
        var closeBtn:* = (g != null && g.ui != null) ? g.ui.btnTargetPortraitClose : null;
        var hasSaved:Boolean = (loadSavedPos() != null);

        if (!_defaultRecorded) {
            if (!hasSaved && _target.x > 100) {
                _defaultX = _target.x;
                _defaultY = _target.y;
                if (closeBtn != null) {
                    var diffX:Number = closeBtn.x - _target.x;
                    var diffY:Number = closeBtn.y - _target.y;
                    if (diffX >= 170.0 && diffX <= 190.0) _closeOffsetX = diffX;
                    if (diffY >= 0.0 && diffY <= 20.0) _closeOffsetY = diffY;
                }
                _defaultRecorded = true;
            } else if (hasSaved && _target.parent != null) {
                _defaultRecorded = true;
                restorePosition();
            }
        } else {
            syncSatellites();
        }
    }

    public function syncSatellites():void {
        if (_target == null) return;
        if (!NativeFramesEnhancer.isMovableTargetEnabled()) return;
        ensureHitLayer();

        if (!_isDragging && _stage == null) {
            if (_hasSavedCache || loadSavedPos() != null) {
                if (_target.x != _savedX || _target.y != _savedY) {
                    _target.x = _savedX;
                    _target.y = _savedY;
                }
            }
        }

        syncCloseButton();
    }

    public function syncCloseButton():void {
        if (_target == null) return;
        if (!NativeFramesEnhancer.isMovableTargetEnabled()) return;
        var g:* = (Main.instance != null) ? Main.instance.getGame() : null;
        if (g == null || g.ui == null) return;

        var closeBtn:* = g.ui.btnTargetPortraitClose;
        if (closeBtn != null) {
            closeBtn.x = _target.x + _closeOffsetX;
            closeBtn.y = _target.y + _closeOffsetY;
        }
    }

    private function onClickCapture(e:MouseEvent):void {
        if (_hasDragged) {
            e.stopImmediatePropagation();
            e.stopPropagation();
        }
    }

    private function onRollOver(e:MouseEvent):void {
        if (!NativeFramesEnhancer.isMovableTargetEnabled()) return;
        drawBackdrop();
    }

    private function onRollOut(e:MouseEvent):void {
        if (!_isDragging) {
            hideBackdrop();
        }
    }

    public function drawBackdrop():void {
        if (!NativeFramesEnhancer.isMovableTargetEnabled()) {
            hideBackdrop();
            return;
        }
        if (_target == null || _target.parent == null || !_target.visible) {
            hideBackdrop();
            return;
        }

        if (_backdrop == null) {
            _backdrop = new Sprite();
            _backdrop.mouseChildren = false;
            _backdrop.mouseEnabled = false;

            _bgShape = new Shape();
            _backdrop.addChild(_bgShape);
        }

        var parent:* = _target.parent;
        if (_backdrop.parent != parent) {
            var idx:int = parent.getChildIndex(_target);
            if (idx + 1 < parent.numChildren) {
                parent.addChildAt(_backdrop, idx + 1);
            } else {
                parent.addChild(_backdrop);
            }
        }

        var vx:Number = _target.x + FRAME_OFFSET_X;
        var vy:Number = _target.y + FRAME_OFFSET_Y;

        // Crisp 1.5px cyan border outline around target frame bounds on hover/drag
        _bgShape.graphics.clear();
        _bgShape.graphics.lineStyle(1.5, 0x38BDF8, 0.85);
        _bgShape.graphics.drawRoundRect(vx, vy, FRAME_WIDTH, FRAME_HEIGHT, 6, 6);

        _backdrop.visible = true;
    }

    public function hideBackdrop():void {
        if (_backdrop != null) {
            if (_bgShape != null) _bgShape.graphics.clear();
            if (_backdrop.parent != null) {
                try { _backdrop.parent.removeChild(_backdrop); } catch (err:Error) {}
            }
            _backdrop.visible = false;
        }
    }

    private function onDown(e:MouseEvent):void {
        if (!NativeFramesEnhancer.isMovableTargetEnabled()) return;
        if (_target == null) return;

        var stg:Stage = _target.stage as Stage;
        if (stg == null) {
            try {
                var g:* = (Main.instance != null) ? Main.instance.getGame() : null;
                if (g != null && g.stage != null) stg = g.stage as Stage;
            } catch (err:Error) {}
        }
        if (stg == null) return;

        _stage = stg;
        _isDragging = false;
        _hasDragged = false;
        _startX = e.stageX;
        _startY = e.stageY;
        _origX = _target.x;
        _origY = _target.y;

        _stage.addEventListener(MouseEvent.MOUSE_MOVE, onMove, false, 0, true);
        _stage.addEventListener(MouseEvent.MOUSE_UP, onUp, false, 0, true);
        _stage.addEventListener(Event.MOUSE_LEAVE, onStageLeave, false, 0, true);
    }

    private function onStageLeave(e:Event):void {
        onUp(null);
    }

    private function onMove(e:MouseEvent):void {
        if (_target == null || _stage == null) {
            stopPress();
            return;
        }

        var dx:Number = e.stageX - _startX;
        var dy:Number = e.stageY - _startY;

        if (!_isDragging) {
            if (dx * dx + dy * dy < THRESHOLD_SQ) return;
            _isDragging = true;

            try {
                var g:* = (Main.instance != null) ? Main.instance.getGame() : null;
                if (g != null && g.ui != null && g.ui.ToolTip != null) {
                    g.ui.ToolTip.close();
                }
            } catch (ttErr:Error) {}
        }

        var newX:Number = Math.round(_origX + dx);
        var newY:Number = Math.round(_origY + dy);

        var stageW:Number = (_stage.stageWidth > 0) ? _stage.stageWidth : 960;
        var stageH:Number = (_stage.stageHeight > 0) ? _stage.stageHeight : 550;
        var maxBottom:Number = stageH - 50;

        var minX:Number = Math.round(-FRAME_OFFSET_X);
        var minY:Number = Math.round(-FRAME_OFFSET_Y);
        var maxX:Number = Math.round(stageW - FRAME_OFFSET_X - FRAME_WIDTH);
        var maxY:Number = Math.round(maxBottom - FRAME_OFFSET_Y - FRAME_HEIGHT);
        if (maxX < minX) maxX = minX;
        if (maxY < minY) maxY = minY;

        if (newX < minX) newX = minX;
        if (newY < minY) newY = minY;
        if (newX > maxX) newX = maxX;
        if (newY > maxY) newY = maxY;

        _target.x = newX;
        _target.y = newY;

        syncCloseButton();
        drawBackdrop();
        e.updateAfterEvent();
    }

    private function onUp(e:MouseEvent):void {
        var wasDragging:Boolean = _isDragging;
        stopPress();
        if (wasDragging) {
            _hasDragged = true;
            savePosition();
            setTimeout(function():void {
                _hasDragged = false;
            }, 150);
        }
        hideBackdrop();
    }

    public function stopPress():void {
        _isDragging = false;
        if (_stage != null) {
            try {
                _stage.removeEventListener(MouseEvent.MOUSE_MOVE, onMove);
                _stage.removeEventListener(MouseEvent.MOUSE_UP, onUp);
                _stage.removeEventListener(Event.MOUSE_LEAVE, onStageLeave);
            } catch (err:Error) {}
            _stage = null;
        }
    }

    private function loadSavedPos():Object {
        if (_hasSavedCache) {
            return { x: _savedX, y: _savedY };
        }
        if (_savedLookupDone) {
            return null;
        }

        try {
            var so:SharedObject = SharedObject.getLocal(SO_NAME);
            if (so != null && so.data != null && so.data.targetX != null && so.data.targetY != null) {
                var sx:Number = Number(so.data.targetX);
                var sy:Number = Number(so.data.targetY);
                if (!isNaN(sx) && !isNaN(sy)) {
                    _hasSavedCache = true;
                    _savedLookupDone = true;
                    _savedX = sx;
                    _savedY = sy;
                    return { x: sx, y: sy };
                }
            }
        } catch (err:Error) {}

        try {
            var g:* = (Main.instance != null) ? Main.instance.getGame() : null;
            if (g != null && g.litePreference != null && g.litePreference.data != null) {
                _savedLookupDone = true;
                if (g.litePreference.data[_litePrefKey] != null) {
                    var lp:* = g.litePreference.data[_litePrefKey];
                    if (lp.x != null && lp.y != null) {
                        var lx:Number = Number(lp.x);
                        var ly:Number = Number(lp.y);
                        if (!isNaN(lx) && !isNaN(ly)) {
                            _hasSavedCache = true;
                            _savedX = lx;
                            _savedY = ly;
                            return { x: lx, y: ly };
                        }
                    }
                }
            }
        } catch (err2:Error) {}

        return null;
    }

    public function savePosition():void {
        if (_target == null) return;
        _hasSavedCache = true;
        _savedLookupDone = true;
        _savedX = _target.x;
        _savedY = _target.y;
        try {
            var so:SharedObject = SharedObject.getLocal(SO_NAME);
            if (so != null && so.data != null) {
                so.data.targetX = _target.x;
                so.data.targetY = _target.y;
                so.flush();
            }
        } catch (err:Error) {}

        try {
            var g:* = (Main.instance != null) ? Main.instance.getGame() : null;
            if (g != null && g.litePreference != null && g.litePreference.data != null) {
                g.litePreference.data[_litePrefKey] = { x: _target.x, y: _target.y };
                g.litePreference.flush();
            }
        } catch (err2:Error) {}
    }

    public function restorePosition():void {
        if (_target == null) return;
        if (!NativeFramesEnhancer.isMovableTargetEnabled()) return;

        var saved:Object = loadSavedPos();
        if (saved != null) {
            _target.x = saved.x;
            _target.y = saved.y;
        }
        syncSatellites();
    }
}
