package skua.remote {

public class LinkedObject {
    private var _id:int;
    private var _parent:*;
    private var _name:String;
    private var _children:*;
    private var _owner:LinkedObject;
    private var _ownerKey:String;

    public function LinkedObject(id:int, parent:*, name:String, owner:LinkedObject = null, ownerKey:String = null) {
        super();
        this._id = id;
        this._parent = parent;
        this._name = name;
        this._children = {};
        this._owner = owner;
        this._ownerKey = ownerKey;
    }

    public function getId():int {
        return this._id;
    }

    public function getValue():* {
        return this._parent[this._name];
    }

    public function setValue(value:*):void {
        this._parent[this._name] = value;
    }

    public function removeChild(name:String):void {
        var linked:LinkedObject = this._children[name];
        if (linked != null) {
            RemoteRegistry.destroy(linked);
            delete this._children[name];
        }
    }

    public function destroyChildren():void {
        for (var name:String in this._children) {
            var linked:LinkedObject = this._children[name];
            if (linked != null) {
                RemoteRegistry.destroy(linked);
            }
            delete this._children[name];
        }
    }

    public function detachFromOwner():void {
        if (this._owner != null) {
            this._owner.removeCachedChild(this._ownerKey, this);
            this._owner = null;
            this._ownerKey = null;
        }
    }

    public function removeCachedChild(key:String, child:LinkedObject):void {
        if (key != null && this._children[key] === child) {
            delete this._children[key];
        }
    }

    public function getChild(path:String):LinkedObject {
        var cached:LinkedObject = this._children[path];
        if (cached != null) {
            return cached;
        }
        var parts:Array = path.split(".");
        var name:String = parts.pop();
        var parent:* = this._parent[_name];
        var partsLen:int = parts.length;
        for (var i:int = 0; i < partsLen; i++) {
            parent = parent[parts[i]];
        }
        return this._children[path] = RemoteRegistry.createLinked(parent, name, this, path);
    }

    public function call(name:String, ...args):LinkedObject {
        var func:Function = this._parent[this._name][name] as Function;
        var result:* = func.apply(null, args);
        var parent:ObjectParent = new ObjectParent(result);
        return RemoteRegistry.createLinked(parent, "_obj");
    }

    public function stringify():String {
        return JSON.stringify(this._parent[this._name]);
    }
}
}
