

using System;
using System.Threading.Tasks;

namespace TVHeadEnd.Helper
{

    public readonly struct Unit
    {
        public static Unit Value => default;
    }

    public class Result<T, TError>
        where T : notnull
        where TError : notnull
    {
        public T Value { get; }
        public TError Error { get; }
        public bool IsSuccess { get; }

        private Result(T value) => (Value, Error, IsSuccess) = (value, default!, true);
        private Result(TError error) => (Value, Error, IsSuccess) = (default!, error, false);

        public static Result<T, TError> Success(T value) => new(value);
        public static Result<T, TError> Failure(TError error) => new(error);

        public static implicit operator Result<T, TError>(T value) => Success(value);
        public static implicit operator Result<T, TError>(TError error) => Failure(error);

        public void Deconstruct(out bool isSuccess, out T value, out TError error)
        {
            value = Value;
            error = Error;
            isSuccess = IsSuccess;
        }

        public TResult Match<TResult>(Func<T, TResult> onSuccess, Func<TError, TResult> onFailure)
        {
            return IsSuccess ? onSuccess(Value) : onFailure(Error);
        }

        // Verkettung
        public Result<TNew, TError> Map<TNew>(Func<T, TNew> mapFunc) where TNew : notnull
        {
            return IsSuccess ? Result<TNew, TError>.Success(mapFunc(Value)) : Result<TNew, TError>.Failure(Error);
        }

        public Result<TNew, TErrorNew> MapBoth<TNew, TErrorNew>(Func<T, TNew> mapFunc, Func<TError, TErrorNew> mapErrorFunc)
            where TNew : notnull
            where TErrorNew : notnull
        {
            return IsSuccess ? Result<TNew, TErrorNew>.Success(mapFunc(Value)) : Result<TNew, TErrorNew>.Failure(mapErrorFunc(Error));
        }

        public Result<T, TErrorNew> MapError<TErrorNew>(Func<TError, TErrorNew> mapErrorFunc) where TErrorNew : notnull
        {
            return IsSuccess ? Result<T, TErrorNew>.Success(Value) : Result<T, TErrorNew>.Failure(mapErrorFunc(Error));
        }

        public Result<TNew, TError> AndThen<TNew>(Func<T, Result<TNew, TError>> bindFunc) where TNew : notnull
        {
            return IsSuccess ? bindFunc(Value) : Result<TNew, TError>.Failure(Error);
        }
        

        public Result<T, TErrorNew> AndThenError<TErrorNew>(Func<TError, Result<T, TErrorNew>> bindFunc) where TErrorNew : notnull
        {
            return IsSuccess ? Result<T, TErrorNew>.Success(Value) : bindFunc(Error);
        }

        public T ValueOr(T defaultValue) => IsSuccess ? Value : defaultValue;

        public Result<T, TError> Tap(Action<T> action)
        {
            if (IsSuccess) action(Value);
            return this;
        }

        public Result<T, TError> TapError(Action<TError> action)
        {
            if (!IsSuccess) action(Error);
            return this;
        }

        public Result<TProjected, TError> SelectMany<TCollection, TProjected>(
            Func<T, Result<TCollection, TError>> collectionSelector,
            Func<T, TCollection, TProjected> resultSelector
        ) 
            where TCollection : notnull
            where TProjected : notnull
        {
            if (!IsSuccess) return Result<TProjected, TError>.Failure(Error);

            var collectionResult = collectionSelector(Value);
            if (!collectionResult.IsSuccess) return Result<TProjected, TError>.Failure(collectionResult.Error);

            var projectedValue = resultSelector(Value, collectionResult.Value);
            return Result<TProjected, TError>.Success(projectedValue);
        }

    }

    public static class ResultExtensions
    {
        // Ermöglicht es, ein asynchrones Lambda in einer synchronen Result-Kette aufzurufen
        public static async Task<Result<TNew, TError>> AndThenAsync<T, TError, TNew>(
            this Result<T, TError> result,
            Func<T, Task<Result<TNew, TError>>> bindFunc)
            where T : notnull
            where TError : notnull
            where TNew : notnull
        {
            if (!result.IsSuccess) 
                return Result<TNew, TError>.Failure(result.Error);
                
            return await bindFunc(result.Value).ConfigureAwait(false);
        }

        // Verkettet direkt auf dem Task, ohne dass der Aufrufer erst awaiten und das Ergebnis
        // einklammern muss. Ohne diese Überladung liest sich eine Kette aus mehreren
        // asynchronen Schritten in C# unbrauchbar.
        public static async Task<Result<TNew, TError>> AndThenAsync<T, TError, TNew>(
            this Task<Result<T, TError>> resultTask,
            Func<T, Task<Result<TNew, TError>>> bindFunc)
            where T : notnull
            where TError : notnull
            where TNew : notnull
        {
            Result<T, TError> result = await resultTask.ConfigureAwait(false);
            return await result.AndThenAsync(bindFunc).ConfigureAwait(false);
        }

        public static async Task<Result<T, TError>> TapAsync<T, TError>(
            this Task<Result<T, TError>> resultTask,
            Action<T> action)
            where T : notnull
            where TError : notnull
        {
            return (await resultTask.ConfigureAwait(false)).Tap(action);
        }

        // Der synchrone Zwilling von AndThenAsync. Ohne ihn muss jede rein synchrone
        // Zwischenstufe ihr Ergebnis in Task.FromResult einwickeln.
        public static async Task<Result<TNew, TError>> AndThen<T, TError, TNew>(
            this Task<Result<T, TError>> resultTask,
            Func<T, Result<TNew, TError>> bindFunc)
            where T : notnull
            where TError : notnull
            where TNew : notnull
        {
            return (await resultTask.ConfigureAwait(false)).AndThen(bindFunc);
        }

        public static async Task<Result<T, TError>> TapErrorAsync<T, TError>(
            this Task<Result<T, TError>> resultTask,
            Action<TError> action)
            where T : notnull
            where TError : notnull
        {
            return (await resultTask.ConfigureAwait(false)).TapError(action);
        }

        public static async Task<Result<T, TErrorNew>> MapErrorAsync<T, TError, TErrorNew>(
            this Task<Result<T, TError>> resultTask,
            Func<TError, TErrorNew> mapErrorFunc)
            where T : notnull
            where TError : notnull
            where TErrorNew : notnull
        {
            return (await resultTask.ConfigureAwait(false)).MapError(mapErrorFunc);
        }
    }

} // namespace TVHeadEnd.Helper