﻿namespace JuiceLog.Services;

// Cuts the first complete JPEG out of what a camera sends over http: a single picture, an MJPEG stream
// (multipart/x-mixed-replace, or the same parts declared as application/octet-stream) or bare concatenated frames.
// The end of the picture is found by walking the JPEG segments instead of searching for the end marker FF D9, which
// also occurs in the EXIF thumbnail of a phone photo - so neither the multipart boundary nor a Content-Length is needed.
internal static class JpegStreamReader
{
    private static ReadOnlySpan<byte> StartOfImage => [0xFF, 0xD8, 0xFF];

    private enum Walk { NeedMoreData, Complete, Broken }

    // Jpeg is null when the stream ended, or maxBytes were read, without a complete picture
    public static async Task<(byte[]? Jpeg, int BytesRead)> ReadFirstAsync(Stream stream, int maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var length = 0;
        var scanner = new Scanner();
        Range picture;

        while (!scanner.TryFindPicture(buffer.AsSpan(0, length), out picture))
        {
            if (length == buffer.Length)
            {
                if (length >= maxBytes)
                {
                    return (null, length);
                }

                Array.Resize(ref buffer, Math.Min(buffer.Length * 2, maxBytes));
            }

            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
            {
                return (null, length);
            }

            length += read;
        }

        return (buffer[picture], length);
    }

    // Keeps its place between calls, so every byte is looked at about once no matter how small the network reads are.
    private sealed class Scanner
    {
        private int _searchFrom;  // where to look for the next start of a picture
        private int _start = -1;  // start of the picture being walked, -1 while none is found
        private int _position;    // next marker of that picture, or the next byte of its compressed data
        private bool _inScanData;

        public bool TryFindPicture(ReadOnlySpan<byte> data, out Range picture)
        {
            picture = default;
            while (true)
            {
                if (_start < 0)
                {
                    var found = data[_searchFrom..].IndexOf(StartOfImage);
                    if (found < 0)
                    {
                        _searchFrom = Math.Max(_searchFrom, data.Length - (StartOfImage.Length - 1));
                        return false;
                    }

                    _start = _searchFrom + found;
                    _position = _start + 2;
                    _inScanData = false;
                }

                switch (WalkSegments(data))
                {
                    case Walk.Complete:
                        picture = _start.._position;
                        return true;
                    case Walk.NeedMoreData:
                        return false;
                    default:
                        // not a JPEG after all, or a frame damaged in transit - a stream brings the next one
                        _searchFrom = _start + 1;
                        _start = -1;
                        break;
                }
            }
        }

        private Walk WalkSegments(ReadOnlySpan<byte> data)
        {
            while (_position + 1 < data.Length)
            {
                if (_inScanData)
                {
                    // compressed data runs up to the next marker; FF 00 is an escaped data byte, FF D0..D7 a restart marker
                    var next = data[_position..].IndexOf((byte)0xFF);
                    if (next < 0)
                    {
                        _position = data.Length;
                        return Walk.NeedMoreData;
                    }

                    _position += next;
                    if (_position + 1 >= data.Length)
                    {
                        return Walk.NeedMoreData;
                    }

                    if (data[_position + 1] is 0x00 or >= 0xD0 and <= 0xD7)
                    {
                        _position += 2;
                        continue;
                    }

                    _inScanData = false; // a real marker: the end of the picture or the next scan of a progressive JPEG
                }

                if (data[_position] != 0xFF)
                {
                    return Walk.Broken;
                }

                var marker = data[_position + 1];
                switch (marker)
                {
                    case 0xFF: // fill byte in front of a marker
                        _position++;
                        continue;
                    case 0xD9: // end of image
                        _position += 2;
                        return Walk.Complete;
                    case 0x01 or >= 0xD0 and <= 0xD7: // markers without a segment
                        _position += 2;
                        continue;
                    case 0x00 or 0xD8:
                        return Walk.Broken;
                }

                if (_position + 3 >= data.Length)
                {
                    return Walk.NeedMoreData;
                }

                // the segment length counts its own two bytes; start of scan (DA) is followed by the compressed data
                var segmentLength = data[_position + 2] << 8 | data[_position + 3];
                if (segmentLength < 2)
                {
                    return Walk.Broken;
                }

                _position += 2 + segmentLength;
                _inScanData = marker == 0xDA;
            }

            return Walk.NeedMoreData;
        }
    }
}
