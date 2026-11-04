package skua.remote {
public class FunctionCaller {
    private var _obj:*;
    private var _name:String;
    private var _args:Array;

    public function FunctionCaller(obj:*, name:String) {
        this._args = [];
        this._obj = obj;
        this._name = name;
    }

    public function pushArgs(args:Array):void {
        this._args = this._args.concat(args);
    }

    public function clearArgs():void {
        this._args = [];
    }

    public function call():* {
        var func:Function = this._obj[this._name] as Function;
        return func.apply(null, this._args);
    }
}
}
