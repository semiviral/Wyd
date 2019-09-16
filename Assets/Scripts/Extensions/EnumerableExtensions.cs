#region

using System.Collections.Generic;
using System.Linq;

#endregion

namespace Extensions
{
    public static class EnumerableExtensions
    {
        public static bool ContainsAll<T>(this IEnumerable<T> enumerable, IEnumerable<T> lookup)
        {
            return !lookup.Except(enumerable).Any();
        }
    }
}