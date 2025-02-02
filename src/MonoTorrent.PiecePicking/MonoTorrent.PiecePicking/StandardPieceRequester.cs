﻿//
// StandardPieceRequester.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2021 Alan McGovern
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using System;
using System.Collections.Generic;
using System.Linq;

namespace MonoTorrent.PiecePicking
{
    public class StandardPieceRequester : IPieceRequester
    {
        IReadOnlyList<ReadOnlyBitField>? IgnorableBitfields { get; set; }
        Memory<BlockInfo> BlockInfoBufferCache { get; set; }
        Memory<PieceSegment> RequestBufferCache { get; set; }
        BitField? Temp { get; set; }
        IPieceRequesterData? TorrentData { get; set; }

        public bool InEndgameMode { get; private set; }
        IPiecePicker? Picker { get; set; }
        PieceRequesterSettings Settings { get; }

        public StandardPieceRequester (PieceRequesterSettings settings)
            => Settings = settings ?? throw new ArgumentNullException (nameof (settings));

        public void Initialise (IPieceRequesterData torrentData, IReadOnlyList<ReadOnlyBitField> ignoringBitfields)
        {
            IgnorableBitfields = ignoringBitfields;
            TorrentData = torrentData;

            Temp = new BitField (TorrentData.PieceCount);

            IPiecePicker picker = new StandardPicker ();
            if (Settings.AllowRandomised)
                picker = new RandomisedPicker (picker);
            if (Settings.AllowRarestFirst)
                picker = new RarestFirstPicker (picker);
            if (Settings.AllowPrioritisation)
                picker = new PriorityPicker (picker);

            Picker = picker;
            Picker.Initialise (torrentData);
        }

        ReadOnlyBitField ApplyIgnorables (ReadOnlyBitField primary)
        {
            Temp!.From (primary);
            for (int i = 0; i < IgnorableBitfields!.Count; i++)
                Temp.NAnd (IgnorableBitfields[i]);
            return Temp;
        }

        public void AddRequests (IReadOnlyList<IPeerWithMessaging> peers)
        {
            for (int i = 0; i < peers.Count; i++) {
                var peer = peers[i];
                if (peer.SuggestedPieces.Count > 0 || (!peer.IsChoking && peer.AmRequestingPiecesCount == 0))
                    AddRequests (peer, peers);
            }
        }

        public void AddRequests (IPeerWithMessaging peer, IReadOnlyList<IPeerWithMessaging> allPeers)
        {
            int maxRequests = peer.MaxPendingRequests;

            if (!peer.CanRequestMorePieces || Picker == null || TorrentData == null)
                return;

            // This is safe to invoke. 'ContinueExistingRequest' strongly guarantees that a peer will only
            // continue a piece they have initiated. If they're choking then the only piece they can continue
            // will be a fast piece (if one exists!)
            if (!peer.IsChoking || peer.SupportsFastPeer) {
                while (peer.AmRequestingPiecesCount < maxRequests) {
                    PieceSegment? request = Picker.ContinueExistingRequest (peer, 0, peer.BitField.Length - 1);
                    if (request != null)
                        peer.EnqueueRequest (request.Value.ToBlockInfo (TorrentData));
                    else
                        break;
                }
            }

            int count = peer.PreferredRequestAmount (TorrentData.PieceLength);
            if (RequestBufferCache.Length < count)
                RequestBufferCache = new Memory<PieceSegment> (new PieceSegment[count]);
            if (BlockInfoBufferCache.Length < count)
                BlockInfoBufferCache = new Memory<BlockInfo> (new BlockInfo[count]);
            // Reuse the same buffer across multiple requests. However ensure the piecepicker is given
            // a Span<T> of the expected size - so slice the reused buffer if it's too large.
            var requestBuffer = RequestBufferCache.Span.Slice (0, count);
            if (!peer.IsChoking || (peer.SupportsFastPeer && peer.IsAllowedFastPieces.Count > 0)) {
                ReadOnlyBitField filtered = null!;
                while (peer.AmRequestingPiecesCount < maxRequests) {
                    filtered ??= ApplyIgnorables (peer.BitField);
                    int requests = Picker.PickPiece (peer, filtered, allPeers, 0, TorrentData.PieceCount - 1, requestBuffer);
                    if (requests > 0)
                        peer.EnqueueRequests (requestBuffer.Slice (0, requests).ToBlockInfo (BlockInfoBufferCache.Span, TorrentData));
                    else
                        break;
                }
            }

            if (!peer.IsChoking && peer.AmRequestingPiecesCount == 0) {
                while (peer.AmRequestingPiecesCount < maxRequests) {
                    PieceSegment? request = Picker.ContinueAnyExistingRequest (peer, 0, TorrentData.PieceCount - 1, 1);
                    // If this peer is a seeder and we are unable to request any new blocks, then we should enter
                    // endgame mode. Every block has been requested at least once at this point.
                    if (request == null && (InEndgameMode || peer.IsSeeder)) {
                        request = Picker.ContinueAnyExistingRequest (peer, 0, TorrentData.PieceCount - 1, 2);
                        InEndgameMode |= request != null;
                    }

                    if (request != null)
                        peer.EnqueueRequest (request.Value.ToBlockInfo (TorrentData));
                    else
                        break;
                }
            }
        }

        public bool ValidatePiece (IPeer peer, BlockInfo blockInfo, out bool pieceComplete, out IList<IPeer> peersInvolved)
        {
            pieceComplete = false;
            peersInvolved = Array.Empty<IPeer> ();
            return Picker != null && Picker.ValidatePiece (peer, blockInfo.ToPieceSegment (), out pieceComplete, out peersInvolved);
        }

        public bool IsInteresting (IPeer peer, ReadOnlyBitField bitfield)
            => Picker != null && Picker.IsInteresting (peer, bitfield);

        public IList<BlockInfo> CancelRequests (IPeer peer, int startIndex, int endIndex)
            => Picker == null ? Array.Empty<BlockInfo> () : Picker.CancelRequests (peer, startIndex, endIndex).Select (t => t.ToBlockInfo (TorrentData!)).ToArray ();

        public void RequestRejected (IPeer peer, BlockInfo pieceRequest)
            => Picker?.RequestRejected (peer, pieceRequest.ToPieceSegment ());

        public int CurrentRequestCount ()
            => Picker == null ? 0 : Picker.CurrentRequestCount ();

    }

    static class PieceRequesterExtensions
    {
        public static PieceSegment ToPieceSegment (this BlockInfo blockInfo)
            => new PieceSegment (blockInfo.PieceIndex, blockInfo.StartOffset / Constants.BlockSize);

        public static BlockInfo ToBlockInfo (this PieceSegment pieceSegment, IPieceRequesterData info)
        {
            var totalBlocks = info.BlocksPerPiece (pieceSegment.PieceIndex);
            var size = pieceSegment.BlockIndex == totalBlocks - 1 ? info.BytesPerPiece (pieceSegment.PieceIndex) - (pieceSegment.BlockIndex) * Constants.BlockSize : Constants.BlockSize;
            return new BlockInfo (pieceSegment.PieceIndex, pieceSegment.BlockIndex * Constants.BlockSize, size);
        }

        public static Span<BlockInfo> ToBlockInfo (this Span<PieceSegment> segments, Span<BlockInfo> blocks, IPieceRequesterData info)
        {
            for (int i = 0; i < segments.Length; i++)
                blocks[i] = segments[i].ToBlockInfo (info);
            return blocks.Slice (0, segments.Length);
        }

        public static Span<PieceSegment> ToPieceSegment (this Span<BlockInfo> blocks, Span<PieceSegment> segments)
        {
            for (int i = 0; i < segments.Length; i++)
                segments[i] = blocks[i].ToPieceSegment ();
            return segments.Slice (0, segments.Length);
        }
    }
}
