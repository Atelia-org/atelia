
```csharp
enum DurableObjectType: byte {
    Invalid = 0,
    DictBase = 1
}

[Flags]
enum KeyValuePairType: byte {
    ValueTypeMask = 0b00111111,    
    Val_Null = 0,
    Val_ObjRef = 1,
    Val_Tombstone = 2,
    Val_VarInt = 3,

    KeyTypeMask = 0b11000000,
    Key_VarInt = 0*(ValueTypeMask+1),
    Key_Int32 = 1*(ValueTypeMask+1),
    Key_Int64 = 2*(ValueTypeMask+1)
}


```

单个DictInt32的文件布局。与内存布局独立。一次性全部反序列化出来。
```
DurableType.DictBase
BaseDict: varint
KeyBase: varint
PairCount: varint
PairTable: PairCount 个 KVPair
  KVPair:
    KeyValuePairType
    Key
    Value
```

多个对象相互引用的文件布局
```

```