namespace HttpProxy
{
    internal static class ArrayDeconstructExtension
    {
        public static void Deconstruct<T>(this T[] arr, out T item1, out T item2)
        {
            item1 = arr[0];
            item2 = arr[1];
        }

        public static void Deconstruct<T>(this T[] arr, out T item1, out T item2, out T item3)
        {
            item1 = arr[0];
            item2 = arr[1];
            item3 = arr[2];
        }
    }
}
