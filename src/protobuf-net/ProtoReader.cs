﻿
using ProtoBuf.Meta;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace ProtoBuf
{
    /// <summary>
    /// A stateful reader, used to read a protobuf stream. Typical usage would be (sequentially) to call
    /// ReadFieldHeader and (after matching the field) an appropriate Read* method.
    /// </summary>
    public abstract partial class ProtoReader : IDisposable
    {
        internal const string UseStateAPI = "If possible, please use the State API; a transitionary implementation is provided, but this API may be removed in a future version";
        private TypeModel _model;
        private int _fieldNumber, _depth;
        private long blockEnd64;
        private NetObjectCache netCache;

        // this is how many outstanding objects do not currently have
        // values for the purposes of reference tracking; we'll default
        // to just trapping the root object
        // note: objects are trapped (the ref and key mapped) via NoteObject
        private uint trapCount; // uint is so we can use beq/bne more efficiently than bgt

        /// <summary>
        /// Gets the number of the field being processed.
        /// </summary>
        public int FieldNumber => _fieldNumber;

        /// <summary>
        /// Indicates the underlying proto serialization format on the wire.
        /// </summary>
        public WireType WireType {
            get;
            private protected set;
        }

        internal const long TO_EOF = -1;

        /// <summary>
        /// Gets / sets a flag indicating whether strings should be checked for repetition; if
        /// true, any repeated UTF-8 byte sequence will result in the same String instance, rather
        /// than a second instance of the same string. Enabled by default. Note that this uses
        /// a <i>custom</i> interner - the system-wide string interner is not used.
        /// </summary>
        public bool InternStrings { get; set; }

        private protected ProtoReader() { }

        /// <summary>
        /// Initialize the reader
        /// </summary>
        internal void Init(TypeModel model, SerializationContext context)
        {
            _model = model;

            if (context == null) { context = SerializationContext.Default; }
            else { context.Freeze(); }
            this.context = context;
            _longPosition = 0;
            _depth = _fieldNumber = 0;

            blockEnd64 = long.MaxValue;
            InternStrings = RuntimeTypeModel.Default.InternStrings;
            WireType = WireType.None;
            trapCount = 1;
            if (netCache == null) netCache = new NetObjectCache();
        }

        private SerializationContext context;

        /// <summary>
        /// Addition information about this deserialization operation.
        /// </summary>
        public SerializationContext Context => context;

        /// <summary>
        /// Releases resources used by the reader, but importantly <b>does not</b> Dispose the 
        /// underlying stream; in many typical use-cases the stream is used for different
        /// processes, so it is assumed that the consumer will Dispose their stream separately.
        /// </summary>
        public virtual void Dispose()
        {
            _model = null;
            if (stringInterner != null)
            {
                stringInterner.Clear();
                stringInterner = null;
            }
            if (netCache != null) netCache.Clear();
        }

        private static uint ImplReadFieldHeader(ProtoReader reader, ref State state)
        {
            if (state.RemainingInCurrent >= 5)
            {
                reader.Advance(state.ReadVarintUInt32(out var tag));
                return tag;
            }
            return reader.FallbackReadFieldHeader(ref state);
        }
        private protected virtual uint FallbackReadFieldHeader(ref State state)
        {
            int read = ImplTryReadUInt32VarintWithoutMoving(this, ref state, out uint value);
            if (read == 0) return 0;
            ImplSkipBytes(this, ref state, read);
            return value;
        }

        private static int ImplReadInt32Varint(ProtoReader reader, ref State state)
        {
            ulong val;
            if (state.RemainingInCurrent >= 10)
            {
                reader.Advance(state.ReadVarintUInt64(out val));
            }
            else
            {
                val = reader.FallbackReadUInt64Varint(ref state);
            }
            return checked((int)unchecked((long)val));
        }
        private static uint ImplReadUInt32Varint(ProtoReader reader, ref State state)
        {
            if (state.RemainingInCurrent >= 5)
            {
                reader.Advance(state.ReadVarintUInt32(out var val));
                return val;
            }
            return reader.FallbackUInt32Varint(ref state);
        }
        private protected virtual uint FallbackUInt32Varint(ref State state)
        {
            int read = ImplTryReadUInt32VarintWithoutMoving(this, ref state, out uint value);
            if (read == 0) ThrowEoF(this);
            ImplSkipBytes(this, ref state, read);
            return value;
        }

        private static ulong ImplReadUInt64Varint(ProtoReader reader, ref State state)
        {
            if (state.RemainingInCurrent >= 10)
            {
                var bytes = state.ReadVarintUInt64(out var val);
                reader.Advance(bytes);
                return val;
            }
            return reader.FallbackReadUInt64Varint(ref state);
        }

        private protected abstract ulong FallbackReadUInt64Varint(ref State state);

        /// <summary>
        /// Returns the position of the current reader (note that this is not necessarily the same as the position
        /// in the underlying stream, if multiple readers are used on the same stream)
        /// </summary>
        public int Position { get { return checked((int)LongPosition); } }

        /// <summary>
        /// Returns the position of the current reader (note that this is not necessarily the same as the position
        /// in the underlying stream, if multiple readers are used on the same stream)
        /// </summary>
        public long LongPosition => _longPosition;
        private long _longPosition;

        private protected void Advance(long count) => _longPosition += count;

        /// <summary>
        /// Reads a signed 16-bit integer from the stream: Variant, Fixed32, Fixed64, SignedVariant
        /// </summary>
        [Obsolete(UseStateAPI, false)]
        public short ReadInt16()
        {
            State state = default;
            return ReadInt16(ref state);
        }

        /// <summary>
        /// Reads a signed 16-bit integer from the stream: Variant, Fixed32, Fixed64, SignedVariant
        /// </summary>
        public short ReadInt16(ref State state)
        {
            checked { return (short)ReadInt32(ref state); }
        }

        /// <summary>
        /// Reads an unsigned 16-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64
        /// </summary>
        [Obsolete(UseStateAPI, false)]
        public ushort ReadUInt16()
        {
            State state = default;
            return ReadUInt16(ref state);
        }

        /// <summary>
        /// Reads an unsigned 16-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64
        /// </summary>
        public ushort ReadUInt16(ref State state)
        {
            checked { return (ushort)ReadUInt32(ref state); }
        }

        /// <summary>
        /// Reads an unsigned 8-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64
        /// </summary>
        [Obsolete(UseStateAPI, false)]
        public byte ReadByte()
        {
            State state = default;
            return ReadByte(ref state);
        }

        /// <summary>
        /// Reads an unsigned 8-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64
        /// </summary>
        public byte ReadByte(ref State state)
        {
            checked { return (byte)ReadUInt32(ref state); }
        }

        /// <summary>
        /// Reads a signed 8-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64, SignedVariant
        /// </summary>
        [Obsolete(UseStateAPI, false)]
        public sbyte ReadSByte()
        {
            State state = default;
            return ReadSByte(ref state);
        }

        /// <summary>
        /// Reads a signed 8-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64, SignedVariant
        /// </summary>
        public sbyte ReadSByte(ref State state)
        {
            checked { return (sbyte)ReadInt32(ref state); }
        }

        /// <summary>
        /// Reads an unsigned 32-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64, SignedVariant
        /// </summary>
        [Obsolete(UseStateAPI, false)]
        public uint ReadUInt32()
        {
            State state = default;
            return ReadUInt32(ref state);
        }

        /// <summary>
        /// Reads an unsigned 32-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64, SignedVariant
        /// </summary>
        public uint ReadUInt32(ref State state)
        {
            switch (WireType)
            {
                case WireType.Variant:
                    return ImplReadUInt32Varint(this, ref state);
                case WireType.Fixed32:
                    return ImplReadUInt32Fixed(this, ref state);
                case WireType.Fixed64:
                    ulong val = ImplReadUInt64Fixed(this, ref state);
                    checked { return (uint)val; }
                default:
                    throw CreateWireTypeException();
            }
        }

        /// <summary>
        /// Reads a signed 32-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64, SignedVariant
        /// </summary>
        [Obsolete(UseStateAPI, false)]
        public int ReadInt32()
        {
            State state = default;
            return ReadInt32(ref state);
        }

        /// <summary>
        /// Reads a signed 32-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64, SignedVariant
        /// </summary>
        public int ReadInt32(ref State state)
        {
            switch (WireType)
            {
                case WireType.Variant:
                    return ImplReadInt32Varint(this, ref state);
                case WireType.Fixed32:
                    return (int)ImplReadUInt32Fixed(this, ref state);
                case WireType.Fixed64:
                    long l = ReadInt64(ref state);
                    checked { return (int)l; }
                case WireType.SignedVariant:
                    return Zag(ImplReadUInt32Varint(this, ref state));
                default:
                    throw CreateWireTypeException();
            }
        }

        private static uint ImplReadUInt32Fixed(ProtoReader reader, ref State state)
        {
            if (state.RemainingInCurrent >= 4)
            {
                reader.Advance(4);
                return state.ReadFixedUInt32();
            }
            return reader.FallbackReadUInt32Fixed(ref state);
        }
        private protected abstract uint FallbackReadUInt32Fixed(ref State state);
        private static ulong ImplReadUInt64Fixed(ProtoReader reader, ref State state)
        {
            if (state.RemainingInCurrent >= 8)
            {
                reader.Advance(8);
                return state.ReadFixedUInt64();
            }
            return reader.FallbackReadUInt64Fixed(ref state);
        }

        private protected abstract ulong FallbackReadUInt64Fixed(ref State state);

        private const long Int64Msb = ((long)1) << 63;
        private const int Int32Msb = ((int)1) << 31;
        private protected static int Zag(uint ziggedValue)
        {
            int value = (int)ziggedValue;
            return (-(value & 0x01)) ^ ((value >> 1) & ~ProtoReader.Int32Msb);
        }

        private protected static long Zag(ulong ziggedValue)
        {
            long value = (long)ziggedValue;
            return (-(value & 0x01L)) ^ ((value >> 1) & ~ProtoReader.Int64Msb);
        }

        /// <summary>
        /// Reads a signed 64-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64, SignedVariant
        /// </summary>
        [Obsolete(UseStateAPI, false)]
        public long ReadInt64()
        {
            State state = default;
            return ReadInt64(ref state);
        }

        /// <summary>
        /// Reads a signed 64-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64, SignedVariant
        /// </summary>
        public long ReadInt64(ref State state)
        {
            switch (WireType)
            {
                case WireType.Variant:
                    return (long)ImplReadUInt64Varint(this, ref state);
                case WireType.Fixed32:
                    return (int)ImplReadUInt32Fixed(this, ref state);
                case WireType.Fixed64:
                    return (long)ImplReadUInt64Fixed(this, ref state);
                case WireType.SignedVariant:
                    return Zag(ImplReadUInt64Varint(this, ref state));
                default:
                    throw CreateWireTypeException();
            }
        }

        private Dictionary<string, string> stringInterner;
        private protected string Intern(string value)
        {
            if (value == null) return null;
            if (value.Length == 0) return "";
            if (stringInterner == null)
            {
                stringInterner = new Dictionary<string, string>
                {
                    { value, value }
                };
            }
            else if (stringInterner.TryGetValue(value, out string found))
            {
                value = found;
            }
            else
            {
                stringInterner.Add(value, value);
            }
            return value;
        }

#if COREFX
        private protected static readonly Encoding UTF8 = Encoding.UTF8;
#else
        private protected static readonly UTF8Encoding UTF8 = new UTF8Encoding();
#endif
        /// <summary>
        /// Reads a string from the stream (using UTF8); supported wire-types: String
        /// </summary>
        [Obsolete(UseStateAPI, false)]
        public string ReadString()
        {
            State state = default;
            return ReadString(ref state);
        }

        /// <summary>
        /// Reads a string from the stream (using UTF8); supported wire-types: String
        /// </summary>
        public string ReadString(ref State state)
        {
            if (WireType == WireType.String)
            {
                int bytes = (int)ImplReadUInt32Varint(this, ref state);
                if (bytes == 0) return "";
                var s = ImplReadString(this, ref state, bytes);
                if (InternStrings) { s = Intern(s); }
                return s;
            }
            throw CreateWireTypeException();
        }

        private static string ImplReadString(ProtoReader reader, ref State state, int bytes)
        {
            if(state.RemainingInCurrent >= bytes)
            {
                reader.Advance(bytes);
                return state.ReadString(bytes);
            }
            return reader.FallbackReadString(ref state, bytes);
        }

        private protected abstract string FallbackReadString(ref State state, int bytes);

        /// <summary>
        /// Throws an exception indication that the given value cannot be mapped to an enum.
        /// </summary>
        public void ThrowEnumException(Type type, int value)
        {
            string desc = type == null ? "<null>" : type.FullName;
            throw AddErrorData(new ProtoException("No " + desc + " enum is mapped to the wire-value " + value.ToString()), this);
        }

        private protected Exception CreateWireTypeException()
        {
            return CreateException($"Invalid wire-type ({WireType}); this usually means you have over-written a file without truncating or setting the length; see https://stackoverflow.com/q/2152978/23354");
        }

        private Exception CreateException(string message)
        {
            return AddErrorData(new ProtoException(message), this);
        }

        /// <summary>
        /// Reads a double-precision number from the stream; supported wire-types: Fixed32, Fixed64
        /// </summary>
        [Obsolete(UseStateAPI, false)]
        public double ReadDouble()
        {
            State state = default;
            return ReadDouble(ref state);
        }

        /// <summary>
        /// Reads a double-precision number from the stream; supported wire-types: Fixed32, Fixed64
        /// </summary>
        public double ReadDouble(ref State state)
        {
            switch (WireType)
            {
                case WireType.Fixed32:
                    return ReadSingle(ref state);
                case WireType.Fixed64:
                    long value = ReadInt64(ref state);
#if FEAT_SAFE
                    return BitConverter.Int64BitsToDouble(value);
#else
                    unsafe { return *(double*)&value; }
#endif
                default:
                    throw CreateWireTypeException();
            }
        }

        /// <summary>
        /// Reads (merges) a sub-message from the stream, internally calling StartSubItem and EndSubItem, and (in between)
        /// parsing the message in accordance with the model associated with the reader
        /// </summary>
        [Obsolete(UseStateAPI, false)]
        public static object ReadObject(object value, int key, ProtoReader reader)
        {
            State state = default;
            return ReadTypedObject(value, key, ref state, reader, null);
        }

        /// <summary>
        /// Reads (merges) a sub-message from the stream, internally calling StartSubItem and EndSubItem, and (in between)
        /// parsing the message in accordance with the model associated with the reader
        /// </summary>
        public static object ReadObject(object value, int key, ref State state, ProtoReader reader)
            => ReadTypedObject(value, key, ref state, reader, null);

        internal static object ReadTypedObject(object value, int key, ref State state, ProtoReader reader, Type type)
        {
            if (reader._model == null)
            {
                throw AddErrorData(new InvalidOperationException("Cannot deserialize sub-objects unless a model is provided"), reader);
            }
            SubItemToken token = ProtoReader.StartSubItem(ref state, reader);
            if (key >= 0)
            {
                value = reader._model.Deserialize(ref state, key, value, reader);
            }
            else if (type != null && reader._model.TryDeserializeAuxiliaryType(ref state, reader, DataFormat.Default, Serializer.ListItemTag, type, ref value, true, false, true, false, null))
            {
                // ok
            }
            else
            {
                TypeModel.ThrowUnexpectedType(type);
            }
            ProtoReader.EndSubItem(token, reader);
            return value;
        }

        /// <summary>
        /// Makes the end of consuming a nested message in the stream; the stream must be either at the correct EndGroup
        /// marker, or all fields of the sub-message must have been consumed (in either case, this means ReadFieldHeader
        /// should return zero)
        /// </summary>
        public static void EndSubItem(SubItemToken token, ProtoReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            long value64 = token.value64;
            switch (reader.WireType)
            {
                case WireType.EndGroup:
                    if (value64 >= 0) throw AddErrorData(new ArgumentException("token"), reader);
                    if (-(int)value64 != reader._fieldNumber) throw reader.CreateException("Wrong group was ended"); // wrong group ended!
                    reader.WireType = WireType.None; // this releases ReadFieldHeader
                    reader._depth--;
                    break;
                // case WireType.None: // TODO reinstate once reads reset the wire-type
                default:
                    if (value64 < reader.LongPosition) throw reader.CreateException($"Sub-message not read entirely; expected {value64}, was {reader.LongPosition}");
                    if (reader.blockEnd64 != reader.LongPosition && reader.blockEnd64 != long.MaxValue)
                    {
                        throw reader.CreateException($"Sub-message not read correctly (end {reader.blockEnd64} vs {reader.LongPosition})");
                    }
                    reader.blockEnd64 = value64;
                    reader._depth--;
                    break;
                    /*default:
                        throw reader.BorkedIt(); */
            }
        }

        /// <summary>
        /// Begins consuming a nested message in the stream; supported wire-types: StartGroup, String
        /// </summary>
        /// <remarks>The token returned must be help and used when callining EndSubItem</remarks>
        [Obsolete(UseStateAPI, false)]
        public static SubItemToken StartSubItem(ProtoReader reader)
        {
            State state = default;
            return StartSubItem(ref state, reader);
        }
        /// <summary>
        /// Begins consuming a nested message in the stream; supported wire-types: StartGroup, String
        /// </summary>
        /// <remarks>The token returned must be help and used when callining EndSubItem</remarks>
        public static SubItemToken StartSubItem(ref State state, ProtoReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            switch (reader.WireType)
            {
                case WireType.StartGroup:
                    reader.WireType = WireType.None; // to prevent glitches from double-calling
                    reader._depth++;
                    return new SubItemToken((long)(-reader._fieldNumber));
                case WireType.String:
                    long len = (long)ImplReadUInt64Varint(reader, ref state);
                    if (len < 0) throw AddErrorData(new InvalidOperationException(), reader);
                    long lastEnd = reader.blockEnd64;
                    reader.blockEnd64 = reader.LongPosition + len;
                    reader._depth++;
                    return new SubItemToken(lastEnd);
                default:
                    throw reader.CreateWireTypeException(); // throws
            }
        }

        /// <summary>
        /// Reads a field header from the stream, setting the wire-type and retuning the field number. If no
        /// more fields are available, then 0 is returned. This methods respects sub-messages.
        /// </summary>
        [Obsolete(UseStateAPI, false)]
        public int ReadFieldHeader()
        {
            State state = default;
            return ReadFieldHeader(ref state);
        }

        /// <summary>
        /// Reads a field header from the stream, setting the wire-type and retuning the field number. If no
        /// more fields are available, then 0 is returned. This methods respects sub-messages.
        /// </summary>
        public int ReadFieldHeader(ref State state)
        {
            // at the end of a group the caller must call EndSubItem to release the
            // reader (which moves the status to Error, since ReadFieldHeader must
            // then be called)
            if (blockEnd64 <= LongPosition || WireType == WireType.EndGroup) { return 0; }

            var tag = ImplReadFieldHeader(this, ref state);
            if (tag != 0)
            {
                WireType = (WireType)(tag & 7);
                _fieldNumber = (int)(tag >> 3);
                if (_fieldNumber < 1) throw new ProtoException("Invalid field in source data: " + _fieldNumber.ToString());
            }
            else
            {
                WireType = WireType.None;
                _fieldNumber = 0;
            }
            if (WireType == ProtoBuf.WireType.EndGroup)
            {
                if (_depth > 0) return 0; // spoof an end, but note we still set the field-number
                throw new ProtoException("Unexpected end-group in source data; this usually means the source data is corrupt");
            }
            return _fieldNumber;
        }

        /// <summary>
        /// Looks ahead to see whether the next field in the stream is what we expect
        /// (typically; what we've just finished reading - for example ot read successive list items)
        /// </summary>
        [Obsolete(UseStateAPI, false)]
        public bool TryReadFieldHeader(int field)
        {
            State state = default;
            return TryReadFieldHeader(ref state, field);
        }

        /// <summary>
        /// Looks ahead to see whether the next field in the stream is what we expect
        /// (typically; what we've just finished reading - for example ot read successive list items)
        /// </summary>
        public bool TryReadFieldHeader(ref State state, int field)
        {
            // check for virtual end of stream
            if (blockEnd64 <= LongPosition || WireType == WireType.EndGroup) { return false; }

            int read = ImplTryReadUInt32VarintWithoutMoving(this, ref state, out uint tag);
            WireType tmpWireType; // need to catch this to exclude (early) any "end group" tokens
            if (read > 0 && ((int)tag >> 3) == field
                && (tmpWireType = (WireType)(tag & 7)) != WireType.EndGroup)
            {
                WireType = tmpWireType;
                _fieldNumber = field;
                ImplSkipBytes(this, ref state, read);
                return true;
            }
            return false;
        }

        private static int ImplTryReadUInt32VarintWithoutMoving(ProtoReader reader, ref State state, out uint value)
        {
            if (state.RemainingInCurrent >= 5)
            {
                var snapshot = state;
                return snapshot.ReadVarintUInt32(out value);
            }
            return reader.FallbackTryReadUInt32VarintWithoutMoving(ref state, out value);
        }

        private protected abstract int FallbackTryReadUInt32VarintWithoutMoving(ref State state, out uint value);

        /// <summary>
        /// Get the TypeModel associated with this reader
        /// </summary>
        public TypeModel Model { get { return _model; } }

        /// <summary>
        /// Compares the streams current wire-type to the hinted wire-type, updating the reader if necessary; for example,
        /// a Variant may be updated to SignedVariant. If the hinted wire-type is unrelated then no change is made.
        /// </summary>
        public void Hint(WireType wireType)
        {
#pragma warning disable RCS1218 // Simplify code branching.
            if (WireType == wireType) { }  // fine; everything as we expect
            else if (((int)wireType & 7) == (int)this.WireType)
            {   // the underling type is a match; we're customising it with an extension
                WireType = wireType;
            }
            // note no error here; we're OK about using alternative data
#pragma warning restore RCS1218 // Simplify code branching.
        }

        /// <summary>
        /// Verifies that the stream's current wire-type is as expected, or a specialized sub-type (for example,
        /// SignedVariant) - in which case the current wire-type is updated. Otherwise an exception is thrown.
        /// </summary>
        public void Assert(WireType wireType)
        {
            if (this.WireType == wireType) { }  // fine; everything as we expect
            else if (((int)wireType & 7) == (int)this.WireType)
            {   // the underling type is a match; we're customising it with an extension
                this.WireType = wireType;
            }
            else
            {   // nope; that is *not* what we were expecting!
                throw CreateWireTypeException();
            }
        }

        private static void ImplSkipBytes(ProtoReader reader, ref State state, long count)
        {
            if (state.RemainingInCurrent >= count)
            {
                state.Skip((int)count);
                reader.Advance(count);
            }
            else
            {
                reader.FallbackSkipBytes(ref state, count);
            }
        }
        private protected abstract void FallbackSkipBytes(ref State state, long count);

        /// <summary>
        /// Discards the data for the current field.
        /// </summary>
        [Obsolete(UseStateAPI, false)]
        public void SkipField()
        {
            State state = default;
            SkipField(ref state);
        }

        /// <summary>
        /// Discards the data for the current field.
        /// </summary>
        public void SkipField(ref State state)
        {
            switch (WireType)
            {
                case WireType.Fixed32:
                    ImplSkipBytes(this, ref state, 4);
                    return;
                case WireType.Fixed64:
                    ImplSkipBytes(this, ref state, 8);
                    return;
                case WireType.String:
                    long len = (long)ImplReadUInt64Varint(this, ref state);
                    ImplSkipBytes(this, ref state, len);
                    return;
                case WireType.Variant:
                case WireType.SignedVariant:
                    ImplReadUInt64Varint(this, ref state); // and drop it
                    return;
                case WireType.StartGroup:
                    int originalFieldNumber = this._fieldNumber;
                    _depth++; // need to satisfy the sanity-checks in ReadFieldHeader
                    while (ReadFieldHeader(ref state) > 0) { SkipField(ref state); }
                    _depth--;
                    if (WireType == WireType.EndGroup && _fieldNumber == originalFieldNumber)
                    { // we expect to exit in a similar state to how we entered
                        WireType = ProtoBuf.WireType.None;
                        return;
                    }
                    throw CreateWireTypeException();
                case WireType.None: // treat as explicit errorr
                case WireType.EndGroup: // treat as explicit error
                default: // treat as implicit error
                    throw CreateWireTypeException();
            }
        }

        /// <summary>
        /// Reads an unsigned 64-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64
        /// </summary>
        [Obsolete(UseStateAPI, false)]
        public ulong ReadUInt64()
        {
            State state = default;
            return ReadUInt64(ref state);
        }

        /// <summary>
        /// Reads an unsigned 64-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64
        /// </summary>
        public ulong ReadUInt64(ref State state)
        {
            switch (WireType)
            {
                case WireType.Variant:
                    return ImplReadUInt64Varint(this, ref state);
                case WireType.Fixed32:
                    return ImplReadUInt32Fixed(this, ref state);
                case WireType.Fixed64:
                    return ImplReadUInt64Fixed(this, ref state);
                default:
                    throw CreateWireTypeException();
            }
        }

        /// <summary>
        /// Reads a single-precision number from the stream; supported wire-types: Fixed32, Fixed64
        /// </summary>
        [Obsolete(UseStateAPI, false)]
        public float ReadSingle()
        {
            State state = default;
            return ReadSingle(ref state);
        }

        /// <summary>
        /// Reads a single-precision number from the stream; supported wire-types: Fixed32, Fixed64
        /// </summary>
        public float ReadSingle(ref State state)
        {
            switch (WireType)
            {
                case WireType.Fixed32:
                    {
                        int value = ReadInt32(ref state);
#if FEAT_SAFE
                        return BitConverter.ToSingle(BitConverter.GetBytes(value), 0);
#else
                        unsafe { return *(float*)&value; }
#endif
                    }
                case WireType.Fixed64:
                    {
                        double value = ReadDouble(ref state);
                        float f = (float)value;
                        if (float.IsInfinity(f) && !double.IsInfinity(value))
                        {
                            throw AddErrorData(new OverflowException(), this);
                        }
                        return f;
                    }
                default:
                    throw CreateWireTypeException();
            }
        }

        /// <summary>
        /// Reads a boolean value from the stream; supported wire-types: Variant, Fixed32, Fixed64
        /// </summary>
        /// <returns></returns>
        [Obsolete(UseStateAPI, false)]
        public bool ReadBoolean()
        {
            State state = default;
            return ReadBoolean(ref state);
        }

        /// <summary>
        /// Reads a boolean value from the stream; supported wire-types: Variant, Fixed32, Fixed64
        /// </summary>
        /// <returns></returns>
        public bool ReadBoolean(ref State state)
        {
            switch (ReadUInt32(ref state))
            {
                case 0: return false;
                case 1: return true;
                default: throw CreateException("Unexpected boolean value");
            }
        }

        private protected static readonly byte[] EmptyBlob = new byte[0];
        /// <summary>
        /// Reads a byte-sequence from the stream, appending them to an existing byte-sequence (which can be null); supported wire-types: String
        /// </summary>
        [Obsolete(UseStateAPI, false)]
        public static byte[] AppendBytes(byte[] value, ProtoReader reader)
        {
            State state = default;
            return reader.AppendBytes(ref state, value);
        }

        /// <summary>
        /// Reads a byte-sequence from the stream, appending them to an existing byte-sequence (which can be null); supported wire-types: String
        /// </summary>
        public static byte[] AppendBytes(byte[] value, ref State state, ProtoReader reader)
            => reader.AppendBytes(ref state, value);

        private protected byte[] AppendBytes(ref State state, byte[] value)
        {
            {
                switch (WireType)
                {
                    case WireType.String:
                        int len = (int)ImplReadUInt32Varint(this, ref state);
                        WireType = WireType.None;
                        if (len == 0) return value ?? EmptyBlob;
                        int offset;
                        if (value == null || value.Length == 0)
                        {
                            offset = 0;
                            value = new byte[len];
                        }
                        else
                        {
                            offset = value.Length;
                            byte[] tmp = new byte[value.Length + len];
                            Buffer.BlockCopy(value, 0, tmp, 0, value.Length);
                            value = tmp;
                        }
                        ImplReadBytes(this, ref state, new ArraySegment<byte>(value, offset, len));
                        return value;
                    case WireType.Variant:
                        return new byte[0];
                    default:
                        throw CreateWireTypeException();
                }
            }
        }

        private static void ImplReadBytes(ProtoReader reader, ref State state, ArraySegment<byte> target)
        {
            if (state.RemainingInCurrent >= target.Count)
            {
                state.ReadBytes(target);
                reader.Advance(target.Count);
            }
            else reader.FallbackReadBytes(ref state, target);
        }
        private protected abstract void FallbackReadBytes(ref State state, ArraySegment<byte> target);

        //static byte[] ReadBytes(Stream stream, int length)
        //{
        //    if (stream == null) throw new ArgumentNullException("stream");
        //    if (length < 0) throw new ArgumentOutOfRangeException("length");
        //    byte[] buffer = new byte[length];
        //    int offset = 0, read;
        //    while (length > 0 && (read = stream.Read(buffer, offset, length)) > 0)
        //    {
        //        length -= read;
        //    }
        //    if (length > 0) ThrowEoF(null);
        //    return buffer;
        //}
        private static int ReadByteOrThrow(Stream source)
        {
            int val = source.ReadByte();
            if (val < 0) ThrowEoF(null);
            return val;
        }

        /// <summary>
        /// Reads the length-prefix of a message from a stream without buffering additional data, allowing a fixed-length
        /// reader to be created.
        /// </summary>
        public static int ReadLengthPrefix(Stream source, bool expectHeader, PrefixStyle style, out int fieldNumber)
            => ReadLengthPrefix(source, expectHeader, style, out fieldNumber, out int bytesRead);

        /// <summary>
        /// Reads a little-endian encoded integer. An exception is thrown if the data is not all available.
        /// </summary>
        public static int DirectReadLittleEndianInt32(Stream source)
        {
            return ReadByteOrThrow(source)
                | (ReadByteOrThrow(source) << 8)
                | (ReadByteOrThrow(source) << 16)
                | (ReadByteOrThrow(source) << 24);
        }

        /// <summary>
        /// Reads a big-endian encoded integer. An exception is thrown if the data is not all available.
        /// </summary>
        public static int DirectReadBigEndianInt32(Stream source)
        {
            return (ReadByteOrThrow(source) << 24)
                 | (ReadByteOrThrow(source) << 16)
                 | (ReadByteOrThrow(source) << 8)
                 | ReadByteOrThrow(source);
        }

        /// <summary>
        /// Reads a varint encoded integer. An exception is thrown if the data is not all available.
        /// </summary>
        public static int DirectReadVarintInt32(Stream source)
        {
            int bytes = TryReadUInt64Varint(source, out ulong val);
            if (bytes <= 0) ThrowEoF(null);
            return checked((int)val);
        }

        /// <summary>
        /// Reads a string (of a given lenth, in bytes) directly from the source into a pre-existing buffer. An exception is thrown if the data is not all available.
        /// </summary>
        public static void DirectReadBytes(Stream source, byte[] buffer, int offset, int count)
        {
            int read;
            if (source == null) throw new ArgumentNullException(nameof(source));
            while (count > 0 && (read = source.Read(buffer, offset, count)) > 0)
            {
                count -= read;
                offset += read;
            }
            if (count > 0) ThrowEoF(null);
        }

        /// <summary>
        /// Reads a given number of bytes directly from the source. An exception is thrown if the data is not all available.
        /// </summary>
        public static byte[] DirectReadBytes(Stream source, int count)
        {
            byte[] buffer = new byte[count];
            DirectReadBytes(source, buffer, 0, count);
            return buffer;
        }

        /// <summary>
        /// Reads a string (of a given lenth, in bytes) directly from the source. An exception is thrown if the data is not all available.
        /// </summary>
        public static string DirectReadString(Stream source, int length)
        {
            byte[] buffer = new byte[length];
            DirectReadBytes(source, buffer, 0, length);
            return Encoding.UTF8.GetString(buffer, 0, length);
        }

        /// <summary>
        /// Reads the length-prefix of a message from a stream without buffering additional data, allowing a fixed-length
        /// reader to be created.
        /// </summary>
        public static int ReadLengthPrefix(Stream source, bool expectHeader, PrefixStyle style, out int fieldNumber, out int bytesRead)
        {
            if (style == PrefixStyle.None)
            {
                bytesRead = fieldNumber = 0;
                return int.MaxValue; // avoid the long.maxvalue causing overflow
            }
            long len64 = ReadLongLengthPrefix(source, expectHeader, style, out fieldNumber, out bytesRead);
            return checked((int)len64);
        }

        /// <summary>
        /// Reads the length-prefix of a message from a stream without buffering additional data, allowing a fixed-length
        /// reader to be created.
        /// </summary>
        public static long ReadLongLengthPrefix(Stream source, bool expectHeader, PrefixStyle style, out int fieldNumber, out int bytesRead)
        {
            fieldNumber = 0;
            switch (style)
            {
                case PrefixStyle.None:
                    bytesRead = 0;
                    return long.MaxValue;
                case PrefixStyle.Base128:
                    ulong val;
                    int tmpBytesRead;
                    bytesRead = 0;
                    if (expectHeader)
                    {
                        tmpBytesRead = ProtoReader.TryReadUInt64Varint(source, out val);
                        bytesRead += tmpBytesRead;
                        if (tmpBytesRead > 0)
                        {
                            if ((val & 7) != (uint)WireType.String)
                            { // got a header, but it isn't a string
                                throw new InvalidOperationException();
                            }
                            fieldNumber = (int)(val >> 3);
                            tmpBytesRead = ProtoReader.TryReadUInt64Varint(source, out val);
                            bytesRead += tmpBytesRead;
                            if (bytesRead == 0)
                            { // got a header, but no length
                                ThrowEoF(null);
                            }
                            return (long)val;
                        }
                        else
                        { // no header
                            bytesRead = 0;
                            return -1;
                        }
                    }
                    // check for a length
                    tmpBytesRead = ProtoReader.TryReadUInt64Varint(source, out val);
                    bytesRead += tmpBytesRead;
                    return bytesRead < 0 ? -1 : (long)val;

                case PrefixStyle.Fixed32:
                    {
                        int b = source.ReadByte();
                        if (b < 0)
                        {
                            bytesRead = 0;
                            return -1;
                        }
                        bytesRead = 4;
                        return b
                             | (ReadByteOrThrow(source) << 8)
                             | (ReadByteOrThrow(source) << 16)
                             | (ReadByteOrThrow(source) << 24);
                    }
                case PrefixStyle.Fixed32BigEndian:
                    {
                        int b = source.ReadByte();
                        if (b < 0)
                        {
                            bytesRead = 0;
                            return -1;
                        }
                        bytesRead = 4;
                        return (b << 24)
                            | (ReadByteOrThrow(source) << 16)
                            | (ReadByteOrThrow(source) << 8)
                            | ReadByteOrThrow(source);
                    }
                default:
                    throw new ArgumentOutOfRangeException(nameof(style));
            }
        }
        /// <summary>Read a varint if possible</summary>
        /// <returns>The number of bytes consumed; 0 if no data available</returns>
        private static int TryReadUInt64Varint(Stream source, out ulong value)
        {
            value = 0;
            int b = source.ReadByte();
            if (b < 0) { return 0; }
            value = (uint)b;
            if ((value & 0x80) == 0) { return 1; }
            value &= 0x7F;
            int bytesRead = 1, shift = 7;
            while (bytesRead < 9)
            {
                b = source.ReadByte();
                if (b < 0) ThrowEoF(null);
                value |= ((ulong)b & 0x7F) << shift;
                shift += 7;
                bytesRead++;

                if ((b & 0x80) == 0) return bytesRead;
            }
            b = source.ReadByte();
            if (b < 0) ThrowEoF(null);
            if ((b & 1) == 0) // only use 1 bit from the last byte
            {
                value |= ((ulong)b & 0x7F) << shift;
                return ++bytesRead;
            }
            throw new OverflowException();
        }

        private protected abstract bool IsFullyConsumed(ref State state);

        internal void CheckFullyConsumed(ref State state)
        {
            if (!IsFullyConsumed(ref state))
            {
                throw AddErrorData(new ProtoException("Incorrect number of bytes consumed"), this);
            }
        }

        internal static void Seek(Stream source, long count, byte[] buffer)
        {
            if (source.CanSeek)
            {
                source.Seek(count, SeekOrigin.Current);
                count = 0;
            }
            else if (buffer != null)
            {
                int bytesRead;
                while (count > buffer.Length && (bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    count -= bytesRead;
                }
                while (count > 0 && (bytesRead = source.Read(buffer, 0, (int)count)) > 0)
                {
                    count -= bytesRead;
                }
            }
            else // borrow a buffer
            {
                buffer = BufferPool.GetBuffer();
                try
                {
                    int bytesRead;
                    while (count > buffer.Length && (bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        count -= bytesRead;
                    }
                    while (count > 0 && (bytesRead = source.Read(buffer, 0, (int)count)) > 0)
                    {
                        count -= bytesRead;
                    }
                }
                finally
                {
                    BufferPool.ReleaseBufferToPool(ref buffer);
                }
            }
            if (count > 0) ThrowEoF(null);
        }
        internal static Exception AddErrorData(Exception exception, ProtoReader source)
        {
#if !CF && !PORTABLE
            if (exception != null && source != null && !exception.Data.Contains("protoSource"))
            {
                exception.Data.Add("protoSource", string.Format("tag={0}; wire-type={1}; offset={2}; depth={3}",
                    source._fieldNumber, source.WireType, source.LongPosition, source._depth));
            }
#endif
            return exception;
        }

        /// <summary>
        /// Copies the current field into the instance as extension data
        /// </summary>
        [Obsolete(ProtoReader.UseStateAPI, false)]
        public void AppendExtensionData(IExtensible instance)
        {
            ProtoReader.State state = default;
            AppendExtensionData(ref state, instance);
        }

        /// <summary>
        /// Copies the current field into the instance as extension data
        /// </summary>
        public void AppendExtensionData(ref ProtoReader.State state, IExtensible instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            IExtension extn = instance.GetExtensionObject(true);
            bool commit = false;
            // unusually we *don't* want "using" here; the "finally" does that, with
            // the extension object being responsible for disposal etc
            Stream dest = extn.BeginAppend();
            try
            {
                //TODO: replace this with stream-based, buffered raw copying
                using (ProtoWriter writer = ProtoWriter.Create(dest, _model, null))
                {
                    AppendExtensionField(ref state, writer);
                    writer.Close();
                }
                commit = true;
            }
            finally { extn.EndAppend(dest, commit); }
        }

        private void AppendExtensionField(ref ProtoReader.State state, ProtoWriter writer)
        {
            //TODO: replace this with stream-based, buffered raw copying
            ProtoWriter.WriteFieldHeader(_fieldNumber, WireType, writer);
            switch (WireType)
            {
                case WireType.Fixed32:
                    ProtoWriter.WriteInt32(ReadInt32(ref state), writer);
                    return;
                case WireType.Variant:
                case WireType.SignedVariant:
                case WireType.Fixed64:
                    ProtoWriter.WriteInt64(ReadInt64(ref state), writer);
                    return;
                case WireType.String:
                    ProtoWriter.WriteBytes(AppendBytes(null, ref state, this), writer);
                    return;
                case WireType.StartGroup:
                    SubItemToken readerToken = StartSubItem(ref state, this),
                        writerToken = ProtoWriter.StartSubItem(null, writer);
                    while (ReadFieldHeader(ref state) > 0) { AppendExtensionField(ref state, writer); }
                    EndSubItem(readerToken, this);
                    ProtoWriter.EndSubItem(writerToken, writer);
                    return;
                case WireType.None: // treat as explicit errorr
                case WireType.EndGroup: // treat as explicit error
                default: // treat as implicit error
                    throw CreateWireTypeException();
            }
        }

        /// <summary>
        /// Indicates whether the reader still has data remaining in the current sub-item,
        /// additionally setting the wire-type for the next field if there is more data.
        /// This is used when decoding packed data.
        /// </summary>
        public static bool HasSubValue(ProtoBuf.WireType wireType, ProtoReader source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            // check for virtual end of stream
            if (source.blockEnd64 <= source.LongPosition || wireType == WireType.EndGroup) { return false; }
            source.WireType = wireType;
            return true;
        }

        internal int GetTypeKey(ref Type type)
        {
            return _model.GetKey(ref type);
        }

        internal NetObjectCache NetCache => netCache;

        internal Type DeserializeType(string value)
        {
            return TypeModel.DeserializeType(_model, value);
        }

        internal void SetRootObject(object value)
        {
            netCache.SetKeyedObject(NetObjectCache.Root, value);
            trapCount--;
        }

        /// <summary>
        /// Utility method, not intended for public use; this helps maintain the root object is complex scenarios
        /// </summary>
        public static void NoteObject(object value, ProtoReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            if (reader.trapCount != 0)
            {
                reader.netCache.RegisterTrappedObject(value);
                reader.trapCount--;
            }
        }

        /// <summary>
        /// Reads a Type from the stream, using the model's DynamicTypeFormatting if appropriate; supported wire-types: String
        /// </summary>
        [Obsolete(UseStateAPI, false)]
        public Type ReadType()
        {
            State state = default;
            return ReadType(ref state);
        }

        /// <summary>
        /// Reads a Type from the stream, using the model's DynamicTypeFormatting if appropriate; supported wire-types: String
        /// </summary>
        public Type ReadType(ref State state)
        {
            return TypeModel.DeserializeType(_model, ReadString(ref state));
        }

        internal void TrapNextObject(int newObjectKey)
        {
            trapCount++;
            netCache.SetKeyedObject(newObjectKey, null); // use null as a temp
        }

        /// <summary>
        /// Merge two objects using the details from the current reader; this is used to change the type
        /// of objects when an inheritance relationship is discovered later than usual during deserilazation.
        /// </summary>
        public static object Merge(ProtoReader parent, object from, object to)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            TypeModel model = parent.Model;
            SerializationContext ctx = parent.Context;
            if (model == null) throw new InvalidOperationException("Types cannot be merged unless a type-model has been specified");
            using (var ms = new MemoryStream())
            {
                model.Serialize(ms, from, ctx);
                ms.Position = 0;
                return model.Deserialize(ms, to, null);
            }
        }

        internal abstract void Recycle();

        /// <summary>
        /// Create an EOF
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception EoF(ProtoReader reader)
        {
            return AddErrorData(new EndOfStreamException(), reader);
        }

        /// <summary>
        /// throw an EOF
        /// </summary>
        protected static void ThrowEoF(ProtoReader reader)
        {
            throw EoF(reader);
        }

        /// <summary>
        /// Create an Overflow
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception Overflow(ProtoReader reader)
        {
            return AddErrorData(new OverflowException(), reader);
        }

        /// <summary>
        /// Throw an Overflow
        /// </summary>
        protected static void ThrowOverflow(ProtoReader reader)
        {
            throw Overflow(reader);
        }
    }
}
