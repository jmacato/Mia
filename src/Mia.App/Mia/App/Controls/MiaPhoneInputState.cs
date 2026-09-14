// SPDX-License-Identifier: MIT

namespace Mia.App.Controls;

internal sealed class MiaPhoneInputState
{
    readonly Dictionary<long, MiaPhoneKey> _pointerKeys = [];
    readonly Dictionary<int, MiaPhoneKey> _keyboardKeys = [];
    readonly Dictionary<MiaPhoneKey, int> _activeSourceCounts = [];

    internal IEnumerable<MiaPhoneKey> ActiveKeys => _activeSourceCounts.Keys;

    internal MiaPhoneInputPressChange PressPointer(long pointerId, MiaPhoneKey key) =>
        PressSource(_pointerKeys, pointerId, key);

    internal MiaPhoneInputReleaseChange ReleasePointer(long pointerId) =>
        ReleaseSource(_pointerKeys, pointerId);

    internal MiaPhoneInputPressChange PressKeyboard(int sourceKey, MiaPhoneKey key) =>
        PressSource(_keyboardKeys, sourceKey, key);

    internal MiaPhoneInputReleaseChange ReleaseKeyboard(int sourceKey) =>
        ReleaseSource(_keyboardKeys, sourceKey);

    internal bool IsActive(MiaPhoneKey key) =>
        _activeSourceCounts.ContainsKey(key);

    internal void Clear()
    {
        _pointerKeys.Clear();
        _keyboardKeys.Clear();
        _activeSourceCounts.Clear();
    }

    MiaPhoneInputPressChange PressSource<TSource>(
        Dictionary<TSource, MiaPhoneKey> sources,
        TSource source,
        MiaPhoneKey key)
        where TSource : notnull
    {
        MiaPhoneKey? previousKey = sources.TryGetValue(
            source,
            out MiaPhoneKey existingKey)
            ? existingKey
            : null;
        var change = default(MiaPhoneInputPressChange);
        if (previousKey != key)
        {
            bool previousKeyBecameInactive = previousKey is { } prior &&
                RemoveSource(prior);
            sources[source] = key;
            bool keyBecameActive = AddSource(key);
            change = new MiaPhoneInputPressChange(
                true,
                previousKey,
                previousKeyBecameInactive,
                keyBecameActive);
        }
        return change;
    }

    MiaPhoneInputReleaseChange ReleaseSource<TSource>(
        Dictionary<TSource, MiaPhoneKey> sources,
        TSource source)
        where TSource : notnull
    {
        if (!sources.Remove(source, out var key))
        {
            return new MiaPhoneInputReleaseChange(false, default, false);
        }

        return new MiaPhoneInputReleaseChange(true, key, RemoveSource(key));
    }

    bool AddSource(MiaPhoneKey key)
    {
        _activeSourceCounts.TryGetValue(key, out var count);
        _activeSourceCounts[key] = count + 1;
        return count == 0;
    }

    bool RemoveSource(MiaPhoneKey key)
    {
        var count = _activeSourceCounts[key] - 1;
        if (count > 0)
        {
            _activeSourceCounts[key] = count;
            return false;
        }

        _activeSourceCounts.Remove(key);
        return true;
    }
}
