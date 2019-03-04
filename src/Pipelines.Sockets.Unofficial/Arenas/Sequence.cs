﻿using Pipelines.Sockets.Unofficial.Internal;
using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Pipelines.Sockets.Unofficial.Arenas
{
    /// <summary>
    /// Represents a Sequence without needing to know the type at compile-time
    /// </summary>
    public readonly struct Sequence : IEquatable<Sequence>
    {
        // the meaning of the fields here is identical to with Sequence<T>,
        // with the distinction that in the single-segment scenario,
        // the second object will usually be a Type (the element-type,
        // to help with type identification)
        private readonly object _startObj, _endObj;
        private readonly int _startOffsetAndArrayFlag, _endOffsetOrLength;

        internal const int MSB = unchecked((int)(uint)0x80000000);

        /// <summary>
        /// Returns an empty sequence of the supplied type
        /// </summary>
        public static Sequence Empty<T>() => new Sequence(default, typeof(T), default, default);

        /// <summary>
        /// Tests two sequences for equality
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override bool Equals(object obj) => obj is Sequence other && Equals(in other);

        /// <summary>
        /// Tests two sequences for equality
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool IEquatable<Sequence>.Equals(Sequence other) => Equals(in other);

        /// <summary>
        /// Tests two sequences for equality
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(in Sequence other)
            => IsEmpty ? other.IsEmpty // all empty sequences are equal - in part because default is type-less
                : (_startObj == other._startObj
                    & _endObj == other._endObj
                    & _startOffsetAndArrayFlag == other._startOffsetAndArrayFlag
                    & _endOffsetOrLength == other._endOffsetOrLength);

        /// <summary>
        /// Used for equality operations
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode()
            => IsEmpty ? 0 :
            (RuntimeHelpers.GetHashCode(_startObj) ^ _startOffsetAndArrayFlag)
            ^ ~(RuntimeHelpers.GetHashCode(_endObj) ^ _endOffsetOrLength);

        /// <summary>
        /// Summarizes a sequence as a string
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override string ToString() => $"{Length}×{ElementType.Name}";

        /// <summary>
        /// Tests two sequences for equality
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(Sequence x, Sequence y) => x.Equals(in y);

        /// <summary>
        /// Tests two sequences for equality
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(Sequence x, Sequence y) => !x.Equals(in y);

        /// <summary>
        /// Indicates whether the sequence involves multiple segments, vs whether all the data fits into the first segment
        /// </summary>
        public bool IsSingleSegment
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _endObj is Type | _endObj == null;
        }

        /// <summary>
        /// Indicates the number of elements in the sequence
        /// </summary>
        public long Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => IsSingleSegment ? _endOffsetOrLength : MultiSegmentLength();
        }


        long MultiSegmentLength()
        {
            // note that in the multi-segment case, the MSB will be set - as it isn't an array
            return (((ISegment)_endObj).RunningIndex + (_endOffsetOrLength)) // start index
                - (((ISegment)_startObj).RunningIndex + (_startOffsetAndArrayFlag & ~MSB)); // start index
        }

        /// <summary>
        /// Indicates whether the sequence is empty (zero elements)
        /// </summary>
        public bool IsEmpty
        {
            // multi-segment is never empty
            // single-segment (end-obj is Type) is empty if the length is zero
            // a default value (end-obj is null) is empty
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (_endObj is Type & _endOffsetOrLength == 0) | _endObj == null;
        }

        /// <summary>
        /// Indicates the type of element defined by the sequence
        /// </summary>
        public Type ElementType
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                // for all single-segment cases (the majority), the
                // second obj *is* the type
                if (_endObj is Type type) return type;

                // for multi-segment cases, we have an ISegment for this, otherwise: default
                return (_startObj as ISegment)?.ElementType ?? typeof(void);
            }
        }

        /// <summary>
        /// Converts an untyped sequence back to a typed sequence; the type must be correct
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Sequence<T> Cast<T>()
        {
            if (_endObj == null) return default; // default can be cast to anything
            if (ElementType != typeof(T)) Throw.InvalidCast();

            // once checked, just use the trusted ctor, removing the Type
            // from the second object if present
            return new Sequence<T>(_startObj, _endObj is Type ? null : _endObj,
                _startOffsetAndArrayFlag, _endOffsetOrLength);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Sequence(object startObj, object endObj,
            int startOffsetAndArrayFlag, int endOffsetOrLength)
        {
            _startObj = startObj;
            _endObj = endObj;
            _startOffsetAndArrayFlag = startOffsetAndArrayFlag;
            _endOffsetOrLength = endOffsetOrLength;
        }
    }

    /// <summary>
    /// Represents a (possibly non-contiguous) region of memory; the read/write cousin or ReadOnlySequence-T
    /// </summary>
    public readonly partial struct Sequence<T> : IEquatable<Sequence<T>>
    {
        private readonly object _startObj, _endObj;
        private readonly int __startOffsetAndArrayFlag, __endOffsetOrLength;

        /*
        A Sequence can be based on:

        (default)
             _startObj=_endObj=null
             _startOffsetAndArrayFlag=_endOffsetOrLength=0

        T[]
            _startObj={array}
            _endObj=null
            _startOffsetAndArrayFlag=[0]{offset, 31 bits}
            _endOffsetOrLength=[0]{length, 31 bits}

        IMemoryOwner<T>
            _startObj={owner}
            _endObj=null
            _startOffsetAndArrayFlag=[1]{offset, 31 bits}
            _endOffsetOrLength=[0]{length, 31 bits}

        SequenceSegment<T> - single-segment; this is the same as IMemoryOwner<T>
                    (with {owner}==={segment}, since SequenceSegment<T> : IMemoryOwner<T>)

        SequenceSegment<T> - multi-segment
            _startObj={start segment}
            _endObj={end segment}
            _startOffsetAndArrayFlag=[1]{start offset, 31 bits}
            _endOffsetOrLength=[0]{end offset, 31 bits}

        Note that for multi-segment scenarios, there is a complication in that the
        position *just past the end* of one segment is semantically identical to
        the position *at the start* of the next segment; we want position equality
        to work, but we also want single-segment chunks whenever possible, so in
        this scenario (where a multi-segment has an end on the boundary):

        - Sequence<T> will treat it as a single-segment, i.e. length based; this
            includes detecting the second segment as "next, 0"
        - but whenever returning a SequencePosition, we will roll *forwards*, and
            give the position as "next, 0" whenever possible

        The reason for position rolling forward is that it isn't a doubly-linked list,
        so we can't ever roll backwards; so if we want position equality, it must
        be forwards-based.
        */

        /// <summary>
        /// Represents a typed sequence as an untyped sequence
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator Sequence(Sequence<T> sequence)
            => sequence.Untyped();

        /// <summary>
        /// Converts an untyped sequence back to a typed sequence; the type must be correct
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static explicit operator Sequence<T>(Sequence sequence)
            => sequence.Cast<T>();

        /// <summary>
        /// Tests two sequences for equality
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override bool Equals(object obj) => obj is Sequence<T> other && Equals(in other);

        /// <summary>
        /// Tests two sequences for equality
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool IEquatable<Sequence<T>>.Equals(Sequence<T> other) => Equals(in other);


        /// <summary>
        /// Tests two sequences for equality
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(in Sequence<T> other)
            => IsEmpty ? other.IsEmpty // all empty sequences are equal - in part because default is type-less
                : (_startObj == other._startObj
                    & _endObj == other._endObj
                    & __startOffsetAndArrayFlag == other.__startOffsetAndArrayFlag
                    & __endOffsetOrLength == other.__endOffsetOrLength);

        /// <summary>
        /// Used for equality operations
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode()
            => IsEmpty ? 0 :
            (RuntimeHelpers.GetHashCode(_startObj) ^ __startOffsetAndArrayFlag)
            ^ ~(RuntimeHelpers.GetHashCode(_endObj) ^ __endOffsetOrLength);

        /// <summary>
        /// Summaries a sequence as a string
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override string ToString() => $"{Length}×{typeof(T).Name}";

        /// <summary>
        /// Tests two sequences for equality
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(Sequence<T> x, Sequence<T> y) => x.Equals(in y);

        /// <summary>
        /// Tests two sequences for equality
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(Sequence<T> x, Sequence<T> y) => !x.Equals(in y);

        /// <summary>
        /// Converts a typed sequence to a typed read-only-sequence
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator ReadOnlySequence<T>(Sequence<T> sequence)
            => sequence.AsReadOnly();

        /// <summary>
        /// Get a reference to an element by index; note that this *can* have
        /// poor performance for multi-segment sequences, but it is usually satisfactory
        /// </summary>
        public ref T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if ((IsArray & index >= 0) && index < SingleSegmentLength)
                    return ref ((T[])_startObj)[index + StartOffset];
                return ref GetReference(index).Value;
            }
        }

        /// <summary>
        /// Get a reference to an element by index; note that this *can* have
        /// poor performance for multi-segment sequences, but it is usually satisfactory
        /// </summary>
        public ref T this[long index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if ((IsArray & index >= 0) && index < SingleSegmentLength)
                    return ref ((T[])_startObj)[(int)index + StartOffset];
                return ref GetReference(index).Value;
            }
        }

        /// <summary>
        /// Obtains a reference into the segment
        /// </summary>
        public Reference<T> GetReference(long index)
        {
            return (IsSingleSegment & index >= 0) && index < SingleSegmentLength // use the trusted .ctor here; we've checked
                ? new Reference<T>((int)index + StartOffset, _startObj)
                : SlowGetReference(in this, index);

            Reference<T> SlowGetReference(in Sequence<T> sequence, long l_index)
            {
                if (l_index < 0 | l_index >= sequence.Length) Throw.IndexOutOfRange();

                // find the actual segment
                var segment = (SequenceSegment<T>)sequence._startObj;
                var offset = SequenceSegment<T>.GetSegmentPosition(ref segment, l_index);
                return new Reference<T>(segment, offset);
            }

        }

        /// <summary>
        /// Converts a typed sequence to a typed read-only-sequence
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static explicit operator Sequence<T>(ReadOnlySequence<T> readOnlySequence)
        {
            if (TryGetSequence(readOnlySequence, out var sequence)) return sequence;
            Throw.InvalidCast();
            return default; // to make compiler happy
        }

        /// <summary>
        /// Represents a typed sequence as an untyped sequence
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Sequence Untyped() => new Sequence(
            _startObj, _endObj ?? typeof(T),
            __startOffsetAndArrayFlag, __endOffsetOrLength);

        /// <summary>
        /// Converts a typed sequence to a typed read-only-sequence
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySequence<T> AsReadOnly()
        {
            return IsArray ? new ReadOnlySequence<T>((T[])_startObj, StartOffset, SingleSegmentLength)
                : FromSegments(this);

            ReadOnlySequence<T> FromSegments(in Sequence<T> sequence)
            {
                var startObj = sequence._startObj;
                if (startObj == null) return default;
                var startOffset = sequence.StartOffset;
                if (startObj is SequenceSegment<T> startSegment)
                {
                    if (sequence.IsSingleSegment)
                    {
                        return new ReadOnlySequence<T>(startSegment, startOffset, startSegment, startOffset + sequence.SingleSegmentLength);
                    }
                    else
                    {
                        var endSegment = (SequenceSegment<T>)sequence._endObj;
                        return new ReadOnlySequence<T>(startSegment, startOffset, endSegment, sequence.MultiSegmentEndOffset);
                    }
                }

                // otherwise, for IMemoryOwner<T> based sequences, we'll have to rely on ROS's Memory<T> handling, post-slice
                var memory = (((IMemoryOwner<T>)startObj).Memory).Slice(startOffset, sequence.SingleSegmentLength);
                return new ReadOnlySequence<T>(memory);
            }
        }

        private enum PositionKind
        {
            Start, End, Other
        }
        private SequencePosition NormalizePosition(object @object, int integer, PositionKind kind)
        {
            if (@object is SequenceSegment<T> sequence && integer == sequence.Length)
            {
                integer = NormalizeForwards(ref sequence, integer);
                @object = sequence;
            }

            // and for equivalence with ROS; look at the ROS source for what this all means
            switch (kind)
            {
                case PositionKind.Start:
                    // check for a memory-manager-backed owner
                    if (IsSingleSegment && _startObj is IMemoryOwner<T> owner && !(_startObj is SequenceSegment<T>))
                    {
                        integer |= Sequence.MSB;
                    }
                    break;
                case PositionKind.End:
                    if (IsArray)
                    {
                        integer |= Sequence.MSB;
                    }
                    break;
                default:

                    break;
            }

            return new SequencePosition(@object, integer);
        }

        /// <summary>
        /// Calculate the start position of the current sequence
        /// </summary>
        public SequencePosition Start
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => NormalizePosition(_startObj, StartOffset, PositionKind.Start);
        }

        /// <summary>
        /// Calculate the end position of the current sequence
        /// </summary>
        public SequencePosition End
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => IsSingleSegment
                ? NormalizePosition(_startObj, StartOffset + SingleSegmentLength, PositionKind.End)
                : NormalizePosition(_endObj, MultiSegmentEndOffset, PositionKind.End);
        }
        /// <summary>
        /// Calculate a position inside the current sequence
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SequencePosition GetPosition(long offset)
        {

            if (offset > 0 & offset <= (IsSingleSegment ? SingleSegmentLength : RemainingFirstSegmentLength(in this)))
            {
                return NormalizePosition(_startObj, StartOffset + (int)offset, PositionKind.Other);
            }
            return SlowGetPosition(in this, offset);

            int RemainingFirstSegmentLength(in Sequence<T> sequence)
                => ((SequenceSegment<T>)sequence._startObj).Length - sequence.StartOffset;

            SequencePosition SlowGetPosition(in Sequence<T> sequence, long index)
            {
                // note: already ruled out single-segment in-range
                if (index == 0) return sequence.Start;
                var len = sequence.Length;
                if (index == len) return sequence.End;
                if (index < 0 | index > len) Throw.IndexOutOfRange();

                // so must be multi-segment
                Debug.Assert(!sequence.IsSingleSegment);
                var segment = (SequenceSegment<T>)sequence._startObj;
                var integer = SequenceSegment<T>.GetSegmentPosition(ref segment, sequence.StartOffset + index);
                return sequence.NormalizePosition(segment, integer, PositionKind.Other);
            }
        }

        /// <summary>
        /// Obtains a sub-region of a sequence
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Sequence<T> Slice(long start)
        {
            long seqLength = Length;
            if (IsSingleSegment & start >= 0 & start <= seqLength)
            {
                // a single-segment can only ever slice into a single-segment; it *retains* the same array status
                return new Sequence<T>(
                    startObj: _startObj, endObj: null,
                    startOffsetAndArrayFlag: (int)(StartOffset + start) | (__startOffsetAndArrayFlag & Sequence.MSB),
                    endOffsetOrLength: (int)(SingleSegmentLength - start));
            }
            return SlowSlice(start, seqLength - start, seqLength);
        }

        /// <summary>
        /// Obtains a sub-region of a sequence
        /// </summary>
        public Sequence<T> Slice(long start, long length)
        {
            long seqLength = Length;
            if (IsSingleSegment & start >= 0 & length >= 0 & start + length <= seqLength)
            {
                // a single-segment can only ever slice into a single-segment; it *retains* the same array status
                return new Sequence<T>(
                    startObj: _startObj, endObj: null,
                    startOffsetAndArrayFlag: (int)(StartOffset + start) | (__startOffsetAndArrayFlag & Sequence.MSB),
                    endOffsetOrLength: (int)length);
            }
            return SlowSlice(start, length, seqLength);
        }

        private Sequence<T> SlowSlice(long start, long length, long seqLength)
        {
            if (start < 0 | start > seqLength) Throw.ArgumentOutOfRange(nameof(start));
            if (length < 0 | start + length > seqLength) Throw.ArgumentOutOfRange(nameof(length));

            if (IsSingleSegment)
            {
                // a single-segment can only ever slice into a single-segment; it *retains* the same array status
                return new Sequence<T>(
                    startObj: _startObj, endObj: null,
                    startOffsetAndArrayFlag: (int)(StartOffset + start) | (__startOffsetAndArrayFlag & Sequence.MSB),
                    endOffsetOrLength: (int)(SingleSegmentLength - start));
            }

            var startSegment = (SequenceSegment<T>)_startObj;
            var startOffset = SequenceSegment<T>.GetSegmentPosition(ref startSegment, start + StartOffset);

            var endSegment = startSegment; // we can resume where we got to from ^^^, and look another "length" items
            var endOffset = SequenceSegment<T>.GetSegmentPosition(ref endSegment, startOffset + length);

            return new Sequence<T>(startSegment, endSegment, startOffset, endOffset);
        }

        /// <summary>
        /// Attempts to convert a typed read-only-sequence back to a typed sequence; the sequence must have originated from a valid typed sequence
        /// </summary>
        public static bool TryGetSequence(in ReadOnlySequence<T> readOnlySequence, out Sequence<T> sequence)
        {
            SequencePosition start = readOnlySequence.Start, end = readOnlySequence.End;
            object startObj = start.GetObject(), endObj = end.GetObject();
            int startIndex = start.GetInteger() & ~Sequence.MSB, endIndex = end.GetInteger() & ~Sequence.MSB; // ROS uses the MSB internally

            // need to test for sequences *before* IMemoryOwner<T> for non-sequence
            if (startObj is SequenceSegment<T> startSeq && endObj is SequenceSegment<T> endSeq)
            {
                sequence = new Sequence<T>(startSeq, endSeq, startIndex, endIndex);
                return true;
            }

            if (startObj == endObj)
            {
                if (startObj is T[] arr)
                {
                    sequence = new Sequence<T>(arr, startIndex, endIndex - startIndex);
                    return true;
                }
                if (startObj is IMemoryOwner<T> owner)
                {
                    sequence = new Sequence<T>(owner, null, startIndex | Sequence.MSB, endIndex - startIndex);
                    return true;
                }
                if (startObj == null & endObj == null & startIndex == 0 & endIndex == 0)
                {   // looks like a default sequence
                    sequence = default;
                    return true;
                }
            }

            sequence = default;
            return false;
        }

        /// <summary>
        /// Indicates the number of elements in the sequence
        /// </summary>
        public long Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return IsSingleSegment ? SingleSegmentLength : MultiSegmentLength(this);

                long MultiSegmentLength(in Sequence<T> sequence)
                {
                    // note that in the multi-segment case, the MSB will be set - as it isn't an array
                    return (((SequenceSegment<T>)sequence._endObj).RunningIndex + sequence.MultiSegmentEndOffset) // end index
                        - (((SequenceSegment<T>)sequence._startObj).RunningIndex + sequence.StartOffset); // start index
                }
            }
        }

        private int SingleSegmentLength
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                Debug.Assert(IsSingleSegment);
                return __endOffsetOrLength;
            }
        }
        private int MultiSegmentEndOffset
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                Debug.Assert(!IsSingleSegment);
                return __endOffsetOrLength;
            }
        }



        /// <summary>
        /// Indicates whether the sequence involves multiple segments, vs whether all the data fits into the first segment
        /// </summary>
        public bool IsSingleSegment
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _endObj == null;
        }

        internal bool IsArray
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ((__startOffsetAndArrayFlag & Sequence.MSB) == 0) & (_startObj != null);
        }

        private int StartOffset
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => __startOffsetAndArrayFlag & ~Sequence.MSB;
        }

        /// <summary>
        /// Indicates whether the sequence is empty (zero elements)
        /// </summary>
        public bool IsEmpty
        {
            // needs to be single-segment and zero length
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _endObj == null & __endOffsetOrLength == 0;
        }

        /// <summary>
        /// Obtains the first segment, in terms of a memory
        /// </summary>
        public Memory<T> FirstSegment
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => IsArray ? new Memory<T>((T[])_startObj, StartOffset, SingleSegmentLength)
                : SlowFirstSegment();
        }

        private Memory<T> SlowFirstSegment()
        {
            if (_startObj == null) return default;
            var firstMem = ((IMemoryOwner<T>)_startObj).Memory;
            return IsSingleSegment
                ? firstMem.Slice(StartOffset, SingleSegmentLength)
                : firstMem.Slice(StartOffset);
        }

        /// <summary>
        /// Obtains the first segment, in terms of a span
        /// </summary>
        public Span<T> FirstSpan
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => IsArray ? new Span<T>((T[])_startObj, StartOffset, SingleSegmentLength)
                : SlowFirstSegment().Span;
        }

        /// <summary>
        /// Copy the contents of the sequence into a contiguous region
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CopyTo(Span<T> destination)
        {
            if (IsSingleSegment) FirstSpan.CopyTo(destination);
            else if (!TrySlowCopy(destination)) ThrowLengthError();

            void ThrowLengthError()
            {
                Span<int> one = stackalloc int[1];
                one.CopyTo(default); // this should give use the CLR's error text (let's hope it doesn't mention sizes!)
            }
        }

        /// <summary>
        /// If possible, copy the contents of the sequence into a contiguous region
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryCopyTo(Span<T> destination)
            => IsSingleSegment ? FirstSpan.TryCopyTo(destination) : TrySlowCopy(destination);

        private bool TrySlowCopy(Span<T> destination)
        {
            if (destination.Length < Length) return false;

            foreach (var span in Spans)
            {
                span.CopyTo(destination);
                destination = destination.Slice(span.Length);
            }
            return true;
        }

        /// <summary>
        /// Create a new single-segment sequence from a memory
        /// </summary>
        public Sequence(Memory<T> memory)
        {
            // there's a lot of logic in ROS for this; let's just re-use that
            if (!TryGetSequence(new ReadOnlySequence<T>(memory), out this))
            {
                Throw.Argument("It was not possible to create a Sequence from this Memory", nameof(memory));
            }
        }

        /// <summary>
        /// Create a new single-segment sequence from an array
        /// </summary>
        public Sequence(T[] array) : this(array, 0, array?.Length ?? 0) { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Sequence<T> TrustedSingleSegment(IMemoryOwner<T> owner, int offset, int length)
            => new Sequence<T>(owner, null, offset | Sequence.MSB, length);

        /// <summary>
        /// Create a new single-segment sequence from an array
        /// </summary>
        public Sequence(T[] array, int offset, int length)
        {
            // basic parameter check
            if (array == null) Throw.ArgumentNull(nameof(array));
            if (offset < 0) Throw.ArgumentOutOfRange(nameof(offset));
            if (length < 0 | (length + offset > array.Length))
                Throw.ArgumentOutOfRange(nameof(length));

            _startObj = array;
            _endObj = null;
            __startOffsetAndArrayFlag = offset;
            __endOffsetOrLength = length;
            AssertValid();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int NormalizeForwards(ref SequenceSegment<T> segment, int offset)
        {
            Debug.Assert(segment != null && offset >= 0 && offset <= segment.Length,
                "invalid segment");

            // the position *after* the end of one segment is identically start (index 0)
            // of the next segment, assuming there is one
            SequenceSegment<T> next;
            if (offset == segment.Length & (next = segment.Next) != null)
            {
                segment = next;
                offset = 0;
                // also, roll over any empty segments (extremely unlikely, but...)
                while (segment.Length == 0 & (next = segment.Next) != null)
                    segment = next;
            }
            return offset;
        }

        internal bool TryGetSegments(out SequenceSegment<T> startSeq, out SequenceSegment<T> endSeq, out int startOffset, out int endOffset)
        {
            if (_startObj is SequenceSegment<T> segment)
            {
                startSeq = segment;
                startOffset = StartOffset;

                if (IsSingleSegment)
                {
                    endSeq = segment;
                    endOffset = startOffset + SingleSegmentLength;
                }
                else
                {
                    endSeq = (SequenceSegment<T>)_endObj;
                    endOffset = MultiSegmentEndOffset;
                }
                return true;
            }

            startSeq = endSeq = null;
            startOffset = endOffset = 0;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Sequence(
            SequenceSegment<T> startSegment, SequenceSegment<T> endSegment,
            int startOffset, int endOffset)
        {
            int endOffsetOrLength;
            T[] array = null;
            if (startSegment != null)
            {
                // roll both positions forwards (note that this includes debug assertion for validity)
                startOffset = NormalizeForwards(ref startSegment, startOffset);
                if (!ReferenceEquals(startSegment, endSegment))
                {
                    endOffset = NormalizeForwards(ref endSegment, endOffset);

                    // detect "end segment at position 0 is actually the point after the start segment"
                    // as being a single segment
                    if (endOffset == 0 && ReferenceEquals(startSegment.Next, endSegment))
                    {
                        endSegment = startSegment;
                        endOffset = startSegment.Length;
                    }
                }

                // detect single-segment sequences (including the scenario where the seond sequence
                // is index 0 of the next)
                if (ReferenceEquals(startSegment, endSegment))
                {
                    endSegment = null;
                    endOffsetOrLength = endOffset - startOffset;
                }
                else
                {
                    endOffsetOrLength = endOffset;
                }
            }
            else
            {
                endOffsetOrLength = 0;
            }

            if (array == null)
            {
                _startObj = startSegment;
                __startOffsetAndArrayFlag = startOffset | Sequence.MSB;
            }
            else
            {
                _startObj = array;
                __startOffsetAndArrayFlag = startOffset;
            }
            _endObj = endSegment;
            __endOffsetOrLength = endOffsetOrLength;
            AssertValid();
        }

        // trusted ctor for when everything is known
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Sequence(object startObj, object endObj, int startOffsetAndArrayFlag, int endOffsetOrLength)
        {
            _startObj = startObj;
            _endObj = endObj;
            __startOffsetAndArrayFlag = startOffsetAndArrayFlag;
            __endOffsetOrLength = endOffsetOrLength;
            AssertValid();
        }

        partial void AssertValid();

#if DEBUG
        [Conditional("DEBUG")]
        partial void AssertValid() // verify all of our expectations - debug only
        {
            Debug.Assert(__endOffsetOrLength >= 0, "endOffsetOrLength should not be negative");
            if (_startObj == null)
            {
                // default
                Debug.Assert(_endObj == null, "default instance should be all-default");
                Debug.Assert(__startOffsetAndArrayFlag == 0, "default instance should be all-default");
                Debug.Assert(__endOffsetOrLength == 0, "default instance should be all-default");
            }
            else if (IsSingleSegment)
            {
                // single-segment; could be array or owner
                if (_startObj is T[] arr)
                {
                    Debug.Assert(IsArray, "array-flag set incorrectly");
                    Debug.Assert(StartOffset <= arr.Length, "start-offset is out-of-range");
                    Debug.Assert(StartOffset + SingleSegmentLength <= arr.Length, "length is out-of-range");
                }
                else if (_startObj is IMemoryOwner<T> owner)
                {
                    var mem = owner.Memory;
                    Debug.Assert(!IsArray, "array-flag set incorrectly");
                    Debug.Assert(StartOffset <= mem.Length, "start-offset is out-of-range");
                    Debug.Assert(StartOffset + SingleSegmentLength <= mem.Length, "length is out-of-range");
                }
                else
                {
                    Debug.Fail("unexpected start object: " + _startObj.GetType().Name);
                }
            }
            else
            {
                // multi-segment
                SequenceSegment<T> startSeg = _startObj as SequenceSegment<T>,
                    endSeg = _endObj as SequenceSegment<T>;
                Debug.Assert(startSeg != null & endSeg != null, "start and end should be sequence segments");
                Debug.Assert(!ReferenceEquals(startSeg, endSeg), "start and end should be different segments");
                Debug.Assert(startSeg.RunningIndex < endSeg.RunningIndex, "start segment should be earlier");
                Debug.Assert(!IsArray, "array-flag set incorrectly");
                Debug.Assert(StartOffset <= startSeg.Length, "start-offset exceeds length of end segment");
                Debug.Assert(MultiSegmentEndOffset <= endSeg.Length, "end-offset exceeds length of end segment");

                // check we can get from start to end
                do
                {
                    startSeg = startSeg.Next;
                    Debug.Assert(startSeg != null, "end-segment does not follow start-segment in the chain");
                } while (startSeg != endSeg);
            }
        }
#endif
    }
}
