using Sqlity.Core;

namespace Sqlity.Storage.Pages;

public sealed class PageBuffer
{
    private readonly byte[] _buffer;

    public PageBuffer(uint pageNumber)
        : this(pageNumber, new byte[DbConstants.PageSize])
    {
    }

    public PageBuffer(uint pageNumber, byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (buffer.Length != DbConstants.PageSize)
        {
            throw new ArgumentException($"Pages must be exactly {DbConstants.PageSize} bytes.", nameof(buffer));
        }

        PageNumber = pageNumber;
        _buffer = buffer;
    }

    public uint PageNumber { get; }

    public Span<byte> Span => _buffer;

    public ReadOnlySpan<byte> ReadOnlySpan => _buffer;

    public static PageBuffer Create(uint pageNumber, PageType pageType)
    {
        var page = new PageBuffer(pageNumber);
        page.WriteHeader(PageHeader.Create(pageNumber, pageType));
        return page;
    }

    public PageHeader ReadHeader() => PageHeader.ReadFrom(ReadOnlySpan[..PageHeader.Size]);

    public void WriteHeader(in PageHeader header)
    {
        if (header.PageNumber != PageNumber)
        {
            throw new InvalidOperationException($"The page header targets page {header.PageNumber}, but the buffer represents page {PageNumber}.");
        }

        header.WriteTo(Span[..PageHeader.Size]);
    }
}
