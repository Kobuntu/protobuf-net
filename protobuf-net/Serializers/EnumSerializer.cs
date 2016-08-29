#if !NO_RUNTIME
using System;
using System.Diagnostics.Eventing.Reader;
using ProtoBuf.Meta;

#if FEAT_IKVM
using Type = IKVM.Reflection.Type;
using IKVM.Reflection;
#else
using System.Reflection;
#endif

namespace ProtoBuf.Serializers
{
    sealed class EnumSerializer : IProtoSerializer
    {
        public struct EnumPair
        {
            public readonly object RawValue; // note that this is boxing, but I'll live with it
#if !FEAT_IKVM
            public readonly Enum TypedValue; // note that this is boxing, but I'll live with it
#endif
            public readonly int WireValue;
            public EnumPair(int wireValue, object raw, Type type)
            {
                WireValue = wireValue;
                RawValue = raw;
#if !FEAT_IKVM
                TypedValue = (Enum)Enum.ToObject(type, raw);
#endif
            }
        } 
        private readonly Type enumType; 
        private readonly EnumPair[] map;
        public EnumSerializer(Type enumType, EnumPair[] map)
        {
            if (enumType == null) throw new ArgumentNullException("enumType");
            this.enumType = enumType;
            this.map = map;
            if (map != null)
            {
                for (int i = 1; i < map.Length; i++)
                for (int j = 0 ; j < i ; j++)
                {
                    if (map[i].WireValue == map[j].WireValue && !Equals(map[i].RawValue, map[j].RawValue))
                    {
                        throw new ProtoException("Multiple enums with wire-value " + map[i].WireValue.ToString());
                    }
                    if (Equals(map[i].RawValue, map[j].RawValue) && map[i].WireValue != map[j].WireValue)
                    {
                        throw new ProtoException("Multiple enums with deserialized-value " + map[i].RawValue);
                    }
                }

            }
        }
        private ProtoTypeCode GetTypeCode() {
            Type type = Helpers.GetUnderlyingType(enumType);
            if(type == null) type = enumType;
            return Helpers.GetTypeCode(type);
        }

        
        public Type ExpectedType { get { return enumType; } }
        
        bool IProtoSerializer.RequiresOldValue { get { return false; } }
        bool IProtoSerializer.ReturnsValue { get { return true; } }

#if !FEAT_IKVM
        private object EnumToWire(object value, out bool isInt64)
        {
            unchecked
            {
                isInt64 = false;
                switch (GetTypeCode())
                { // unbox then convert to int
                    case ProtoTypeCode.Byte: return (byte)value;
                    case ProtoTypeCode.SByte: return (sbyte)value;
                    case ProtoTypeCode.Int16: return (short)value;
                    case ProtoTypeCode.Int32: return value;
                    case ProtoTypeCode.Int64: return (long)value;
                    case ProtoTypeCode.UInt16: return (ushort)value;
                    case ProtoTypeCode.UInt32: return (uint)value;
                    case ProtoTypeCode.UInt64:
                        isInt64 = true;
                        return (ulong)value;
                    default: throw new InvalidOperationException();
                }
            }
        }
        private object WireToEnum(object value)
        {
            unchecked
            {
                switch (GetTypeCode())
                { // convert from int then box 
                    case ProtoTypeCode.Byte: return Enum.ToObject(enumType, (byte)(int)value);
                    case ProtoTypeCode.SByte: return Enum.ToObject(enumType, (sbyte)(int)value);
                    case ProtoTypeCode.Int16: return Enum.ToObject(enumType, (short)(int)value);
                    case ProtoTypeCode.Int32: return Enum.ToObject(enumType, (int)value);
                    case ProtoTypeCode.Int64: return Enum.ToObject(enumType, (long)value);
                    case ProtoTypeCode.UInt16: return Enum.ToObject(enumType, (ushort)(int)value);
                    case ProtoTypeCode.UInt32: return Enum.ToObject(enumType, (uint)(int)value);
                    case ProtoTypeCode.UInt64: return Enum.ToObject(enumType, (ulong)value);
                    default: throw new InvalidOperationException();
                }
            }
        }

        //Proryv
        public object Read(object value, ProtoReader source)
        {
            Helpers.DebugAssert(value == null); // since replaces

            var typeCode = GetTypeCode();
            object wireValue;
            switch (typeCode)
            {
                case ProtoTypeCode.UInt64:
                    wireValue = source.ReadUInt64();
                    break;
                case ProtoTypeCode.Int64:
                    wireValue = source.ReadInt64();
                    break;
                default:
                    wireValue = source.ReadInt32();
                    break;
            }

            //int wireValue = source.ReadInt32();
            if(map == null) {
                return WireToEnum(wireValue);
            }
            for(int i = 0 ; i < map.Length ; i++) {
                //Если будет падать приведение, то вместо (int)wireValue приводить так Convert.ToInt32(wireValue)
                if(map[i].WireValue == (int)wireValue) {
                    return map[i].TypedValue;
                }
            }
            source.ThrowEnumException(ExpectedType, (int)wireValue);
            return null; // to make compiler happy
        }

        //Proryv
        public void Write(object value, ProtoWriter dest)
        {
            if (map == null)
            {
                var typeCode = GetTypeCode();
                switch (typeCode)
                {
                    case ProtoTypeCode.UInt64:
                        ProtoWriter.WriteUInt64((ulong) value, dest);
                        break;
                    case ProtoTypeCode.Int64:
                        ProtoWriter.WriteInt64((long)value, dest);
                        break;
                    default:
                        ProtoWriter.WriteInt32(Convert.ToInt32(value), dest);
                        break;
                }
            }
            else
            {
                for (int i = 0; i < map.Length; i++)
                {
                    if (object.Equals(map[i].TypedValue, value))
                    {
                        ProtoWriter.WriteInt32(map[i].WireValue, dest);
                        return;
                    }
                }
                ProtoWriter.ThrowEnumException(dest, value);
            }
        }
#endif
#if FEAT_COMPILER
        void IProtoSerializer.EmitWrite(Compiler.CompilerContext ctx, Compiler.Local valueFrom)
        {
            ProtoTypeCode typeCode = GetTypeCode();
            if (map == null)
            {
                ctx.LoadValue(valueFrom);
                ctx.ConvertToInt32(typeCode, false);
                ctx.EmitBasicWrite("WriteInt32", null);
            }
            else
            {
                using (Compiler.Local loc = ctx.GetLocalWithValue(ExpectedType, valueFrom))
                {
                    Compiler.CodeLabel @continue = ctx.DefineLabel();
                    for (int i = 0; i < map.Length; i++)
                    {
                        Compiler.CodeLabel tryNextValue = ctx.DefineLabel(), processThisValue = ctx.DefineLabel();
                        ctx.LoadValue(loc);
                        WriteEnumValue(ctx, typeCode, map[i].RawValue);
                        ctx.BranchIfEqual(processThisValue, true);
                        ctx.Branch(tryNextValue, true);
                        ctx.MarkLabel(processThisValue);
                        ctx.LoadValue(map[i].WireValue);
                        ctx.EmitBasicWrite("WriteInt32", null);
                        ctx.Branch(@continue, false);
                        ctx.MarkLabel(tryNextValue);
                    }
                    ctx.LoadReaderWriter();
                    ctx.LoadValue(loc);
                    ctx.CastToObject(ExpectedType);
                    ctx.EmitCall(ctx.MapType(typeof(ProtoWriter)).GetMethod("ThrowEnumException"));
                    ctx.MarkLabel(@continue);
                }
            }
            
        }
        void IProtoSerializer.EmitRead(Compiler.CompilerContext ctx, Compiler.Local valueFrom)
        {
            ProtoTypeCode typeCode = GetTypeCode();
            if (map == null)
            {
                ctx.EmitBasicRead("ReadInt32", ctx.MapType(typeof(int)));
                ctx.ConvertFromInt32(typeCode, false);
            }
            else
            {
                int[] wireValues = new int[map.Length];
                object[] values = new object[map.Length];
                for (int i = 0; i < map.Length; i++)
                {
                    wireValues[i] = map[i].WireValue;
                    values[i] = map[i].RawValue;
                }
                using (Compiler.Local result = new Compiler.Local(ctx, ExpectedType))
                using (Compiler.Local wireValue = new Compiler.Local(ctx, ctx.MapType(typeof(int))))
                {
                    ctx.EmitBasicRead("ReadInt32", ctx.MapType(typeof(int)));
                    ctx.StoreValue(wireValue);
                    Compiler.CodeLabel @continue = ctx.DefineLabel();
                    foreach (BasicList.Group group in BasicList.GetContiguousGroups(wireValues, values))
                    {
                        Compiler.CodeLabel tryNextGroup = ctx.DefineLabel();
                        int groupItemCount = group.Items.Count;
                        if (groupItemCount == 1)
                        {
                            // discreet group; use an equality test
                            ctx.LoadValue(wireValue);
                            ctx.LoadValue(group.First);
                            Compiler.CodeLabel processThisValue = ctx.DefineLabel();
                            ctx.BranchIfEqual(processThisValue, true);
                            ctx.Branch(tryNextGroup, false);
                            WriteEnumValue(ctx, typeCode, processThisValue, @continue, group.Items[0], @result);
                        }
                        else
                        {
                            // implement as a jump-table-based switch
                            ctx.LoadValue(wireValue);
                            ctx.LoadValue(group.First);
                            ctx.Subtract(); // jump-tables are zero-based
                            Compiler.CodeLabel[] jmp = new Compiler.CodeLabel[groupItemCount];
                            for (int i = 0; i < groupItemCount; i++) {
                                jmp[i] = ctx.DefineLabel();
                            }
                            ctx.Switch(jmp);
                            // write the default...
                            ctx.Branch(tryNextGroup, false);
                            for (int i = 0; i < groupItemCount; i++)
                            {
                                WriteEnumValue(ctx, typeCode, jmp[i], @continue, group.Items[i], @result);
                            }
                        }
                        ctx.MarkLabel(tryNextGroup);
                    }
                    // throw source.CreateEnumException(ExpectedType, wireValue);
                    ctx.LoadReaderWriter();
                    ctx.LoadValue(ExpectedType);
                    ctx.LoadValue(wireValue);
                    ctx.EmitCall(ctx.MapType(typeof(ProtoReader)).GetMethod("ThrowEnumException"));
                    ctx.MarkLabel(@continue);
                    ctx.LoadValue(result);
                }
            }
        }
        private static void WriteEnumValue(Compiler.CompilerContext ctx, ProtoTypeCode typeCode, object value)
        {
            switch (typeCode)
            {
                case ProtoTypeCode.Byte: ctx.LoadValue((int)(byte)value); break;
                case ProtoTypeCode.SByte: ctx.LoadValue((int)(sbyte)value); break;
                case ProtoTypeCode.Int16: ctx.LoadValue((int)(short)value); break;
                case ProtoTypeCode.Int32: ctx.LoadValue((int)(int)value); break;
                case ProtoTypeCode.Int64: ctx.LoadValue((long)(long)value); break;
                case ProtoTypeCode.UInt16: ctx.LoadValue((int)(ushort)value); break;
                case ProtoTypeCode.UInt32: ctx.LoadValue((int)(uint)value); break;
                case ProtoTypeCode.UInt64: ctx.LoadValue((long)(ulong)value); break;
                default: throw new InvalidOperationException();
            }
        }
        private static void WriteEnumValue(Compiler.CompilerContext ctx, ProtoTypeCode typeCode, Compiler.CodeLabel handler, Compiler.CodeLabel @continue, object value, Compiler.Local local)
        {
            ctx.MarkLabel(handler);
            WriteEnumValue(ctx, typeCode, value);
            ctx.StoreValue(local);
            ctx.Branch(@continue, false); // "continue"
        }
#endif
    }
}
#endif