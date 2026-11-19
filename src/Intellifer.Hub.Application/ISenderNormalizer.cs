using Intellifer.Hub.Domain;

namespace Intellifer.Hub.Application;

public interface ISenderNormalizer
{
    string Normalize(ChannelType channel, string senderRaw);
}
