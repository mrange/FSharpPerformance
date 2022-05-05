﻿open System
open System.Collections
open System.Collections.Generic
open BenchmarkDotNet.Attributes
open BenchmarkDotNet.Running

[<Measure>] type JobId
[<Measure>] type MachineId
[<Measure>] type OperationId
[<Measure>] type Assignment = JobId * MachineId * OperationId

module Assignment =
    
    let create (jobId: int<JobId>) (machineId: int<MachineId>) (operationId: int<OperationId>) =
        ((int64 jobId) <<< 32) ||| ((int64 machineId) <<< 16) ||| (int64 operationId)
        |> LanguagePrimitives.Int64WithMeasure<Assignment>
        
    let decompose (assignment: int64<Assignment>) =
        let jobId = int (int64 assignment >>> 32) |> LanguagePrimitives.Int32WithMeasure<JobId>
        let machineId = (int assignment) >>> 16 |> LanguagePrimitives.Int32WithMeasure<MachineId>
        let operationId = (int assignment) &&& 0x0000FFFF |> LanguagePrimitives.Int32WithMeasure<OperationId>
        jobId, machineId, operationId


type BitSetTracker (jobCount, machineCount, operationCount: int) =
    let uint64sRequired = ((jobCount * machineCount * operationCount) + 63) / 64
    let values : uint64 array = Array.zeroCreate uint64sRequired
    
    member internal _.JobCount = jobCount
    member internal _.MachineCount = machineCount
    member internal _.OperationCount = operationCount
    member internal _.Values = values
    
    member _.Item
        with get (jobId: int<JobId>, machineId: int<MachineId>, operationId: int<OperationId>) =
            if (int jobId) >= jobCount then
                raise (IndexOutOfRangeException (nameof jobId))
            if (int machineId) >= machineCount then
                raise (IndexOutOfRangeException (nameof machineId))
            if (int operationId) >= operationCount then
                raise (IndexOutOfRangeException (nameof operationId))
            
            let location = (int jobId) * (machineCount * operationCount) + (int machineId) * operationCount + (int operationId)
            // The int64 we will need to lookup
            let bucket = location / 64
            // The bit in the int64 we want to return
            let offset = location - (bucket * 64)
            // Mask to check with
            let mask = 1UL <<< offset
            // Return whether the bit at the offset is set to 1 or not
            values[bucket] &&& mask <> 0UL
            
        and set (jobId: int<JobId>, machineId: int<MachineId>, operationId: int<OperationId>) value =
            if (int jobId) >= jobCount then
                raise (IndexOutOfRangeException (nameof jobId))
            if (int machineId) >= machineCount then
                raise (IndexOutOfRangeException (nameof machineId))
            if (int operationId) >= operationCount then
                raise (IndexOutOfRangeException (nameof operationId))
            
            let location = (int jobId) * (machineCount * operationCount) + (int machineId) * operationCount + (int operationId)
            // The int64 we will need to lookup
            let bucket = location / 64
            // The bit in the int64 we want to update
            let offset = location - (bucket * 64)
            // Get the int representation of the value
            let value = if value then 1UL else 0UL
            // Set the bit in the bucket to the desired value
            values[bucket] <- (values[bucket] &&& ~~~(1UL <<< offset)) ||| (value <<< offset)
    
            
module BitSetTracker =
    
    let map (f: int<JobId> -> int<MachineId> -> int<OperationId> -> 'Result) (b: BitSetTracker) =
        let acc = Stack<'Result> ()
        let mutable i = 0
        let length = b.Values.Length

        while i < length do
            let mutable bitSet = b.Values[i]
            while bitSet <> 0UL do
                let r = System.Numerics.BitOperations.TrailingZeroCount bitSet
                let location = i * 64 + r
                let jobId =
                    location / (b.MachineCount * b.OperationCount)
                    |> LanguagePrimitives.Int32WithMeasure<JobId>
                let machineId =
                    (location - (int jobId) * (b.MachineCount * b.OperationCount)) / b.OperationCount
                    |> LanguagePrimitives.Int32WithMeasure<MachineId>
                let operationId =
                    location - (int jobId) * (b.MachineCount * b.OperationCount) - (int machineId) * b.OperationCount
                    |> LanguagePrimitives.Int32WithMeasure<OperationId>
                
                let result = f jobId machineId operationId
                acc.Push result
                
                bitSet <- bitSet ^^^ (1UL <<< r)
                
            i <- i + 1
                
        acc.ToArray()
        
  
[<MemoryDiagnoser>]
type Benchmarks () =
    
    let rng = Random 123
    let jobIdBound = 1_000
    let machineIdBound = 10
    let operationIdBound = 100
    let valueCount = 2_000
    
    let values =
        [|for _ in 1..valueCount ->
            let jobId = rng.Next jobIdBound |> LanguagePrimitives.Int32WithMeasure<JobId>
            let machineId = rng.Next machineIdBound |> LanguagePrimitives.Int32WithMeasure<MachineId>
            let operationId = rng.Next operationIdBound |> LanguagePrimitives.Int32WithMeasure<OperationId>
            struct (jobId, machineId, operationId)
        |]
        
    let removeCount = 2
    let removeValues =
        [|for _ in 1..removeCount ->
            values[rng.Next values.Length]  
        |]
        
    let addCount = 2
    let addValues =
        [|for _ in 1..addCount ->
            let jobId = rng.Next jobIdBound |> LanguagePrimitives.Int32WithMeasure<JobId>
            let machineId = rng.Next machineIdBound |> LanguagePrimitives.Int32WithMeasure<MachineId>
            let operationId = rng.Next operationIdBound |> LanguagePrimitives.Int32WithMeasure<OperationId>
            struct (jobId, machineId, operationId)
        |]
        
    let hashSet =
        values
        |> Array.map (fun struct (jobId, machineId, operationId) -> Assignment.create jobId machineId operationId)
        |> HashSet
        
    let bitSet =
        let b = BitSetTracker (jobIdBound, machineIdBound, operationIdBound)
        for jobId, machineId, operationId in values do
            b.[jobId, machineId, operationId] <- true   
        b
            
            
    [<Benchmark>]
    member _.HashSetAdd () =
        
        for jobId, machineId, operationId in addValues do
            let assignment = Assignment.create jobId machineId operationId
            hashSet.Add assignment |> ignore
        
        
    [<Benchmark>]
    member _.BitSetAdd () =
        
        for jobId, machineId, operationId in addValues do
            bitSet[jobId, machineId, operationId] <- true
            
            
    [<Benchmark>]
    member _.HashSetRemove () =
        
        for jobId, machineId, operationId in removeValues do
            let assignment = Assignment.create jobId machineId operationId
            hashSet.Remove assignment |> ignore
            
            
    [<Benchmark>]
    member _.BitSetRemove () =
        
        for jobId, machineId, operationId in removeValues do
            bitSet[jobId, machineId, operationId] <- false
            
            
    [<Benchmark>]
    member _.HashSetMap () =
        
        hashSet
        |> Seq.map Assignment.decompose
        |> Seq.toArray
        
        
    [<Benchmark>]
    member _.BitSetMap () =
        
        bitSet
        |> BitSetTracker.map (fun a b c -> a, b, c)
        
            
        
let _ = BenchmarkRunner.Run<Benchmarks>()
