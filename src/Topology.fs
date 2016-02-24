﻿module Topology

open Common.Error
open Common.Format
open QuickGraph
open QuickGraph.Algorithms
open System.Xml
open FSharp.Data
open System.Collections.Generic

type NodeType = 
    | Start
    | End
    | Outside
    | Inside 
    | InsideOriginates
    | Unknown

[<Struct>]
type Node =
    val Loc: string 
    val Typ: NodeType
    new(l,t) = {Loc = l; Typ = t}


type T = BidirectionalGraph<Node,Edge<Node>>


let copyTopology (topo: T) : T = 
    let newTopo = BidirectionalGraph<Node,Edge<Node>>()
    for v in topo.Vertices do newTopo.AddVertex v |> ignore
    for e in topo.Edges do newTopo.AddEdge e |> ignore 
    newTopo

let alphabet (topo: T) : Set<Node> * Set<Node> = 
    let mutable ain = Set.empty 
    let mutable aout = Set.empty 
    for v in topo.Vertices do
        match v.Typ with 
        | Inside | InsideOriginates -> ain <- Set.add v ain
        | Outside | Unknown -> aout <- Set.add v aout
        | Start | End -> failwith "unreachable"
    (ain, aout)

let isTopoNode (t: Node) = 
    match t.Typ with 
    | Start | End -> false
    | _ -> true

let isOutside (t: Node) = 
    match t.Typ with 
    | Outside -> true
    | Unknown -> true
    | _ -> false

let isInside (t: Node) = 
    match t.Typ with 
    | Inside -> true 
    | InsideOriginates ->  true 
    | _ -> false

let canOriginateTraffic (t: Node) = 
    match t.Typ with 
    | InsideOriginates -> true 
    | Outside -> true
    | Unknown -> true
    | Inside -> false
    | Start | End -> false

let isWellFormed (topo: T) : bool =
    let onlyInside = copyTopology topo
    onlyInside.RemoveVertexIf (fun v -> isOutside v) |> ignore
    let d = Dictionary<Node,int>()
    ignore (onlyInside.WeaklyConnectedComponents d)
    (Set.ofSeq d.Values).Count = 1

let rec addVertices (topo: T) (vs: Node list) = 
    match vs with 
    | [] -> ()
    | v::vs -> 
        topo.AddVertex v |> ignore
        addVertices topo vs

let rec addEdgesUndirected (topo: T) (es: (Node * Node) list) =
    match es with 
    | [] -> () 
    | (x,y)::es -> 
        let e1 = Edge(x,y)
        let e2 = Edge(y,x)
        ignore (topo.AddEdge e1)
        ignore (topo.AddEdge e2)
        addEdgesUndirected topo es

let rec addEdgesDirected (topo: T) (es: (Node * Node) list) = 
    match es with 
    | [] -> () 
    | (x,y)::es -> 
        let e = Edge(x,y)
        ignore (topo.AddEdge e)
        addEdgesDirected topo es

let getStateByLoc (topo: T) loc = 
    topo.Vertices |> Seq.tryFind (fun v -> v.Loc = loc)
    
let findLinks (topo: T) (froms, tos) =
    let mutable pairs = []
    for x in Set.toSeq froms do 
        for y in Set.toSeq tos do 
            let a = getStateByLoc topo x 
            let b = getStateByLoc topo y
            match a,b with 
            | Some a, Some b -> 
                let ns = 
                    topo.OutEdges a
                    |> Seq.map (fun (e: Edge<Node>) -> e.Target)
                    |> Set.ofSeq
                if Set.contains b ns then 
                    pairs <- (a, b) :: pairs
            | _, _ -> ()
    pairs

type Topo = XmlProvider<"../examples/dc.xml">

type TopoInfo =
    {Graph: T; 
     AsnMap: Map<string, uint32>;
     InternalNames: Set<string>;
     ExternalNames: Set<string>;
     AllNames: Set<string>}

let inline getAsn name asn =
    if asn < 0 then 
        error (sprintf "Negate AS number '%d' in topology for node '%s'" asn name)
    else uint32 asn

let router (asn:string) (ti:TopoInfo) = 
    let inline eqAsn _ v = string v = asn
    match Map.tryFindKey eqAsn ti.AsnMap with
    | None -> "as" + asn
    | Some r -> r

let readTopology (file: string) : TopoInfo =
    let g = BidirectionalGraph<Node,Edge<Node>>()
    // avoid adding edges twice
    let seen = HashSet()
    let inline addEdge x y = 
        if not (seen.Contains (x,y)) then
            seen.Add (x,y) |> ignore
            let e = Edge(x,y)
            ignore (g.AddEdge e)
    let topo = Topo.Load file
    let mutable nodeMap = Map.empty
    let mutable asnMap = Map.empty
    let mutable internalNames = Set.empty
    let mutable externalNames = Set.empty
    for n in topo.Nodes do
        match Map.tryFind n.Name asnMap with
        | None -> asnMap <- Map.add n.Name (getAsn n.Name n.Asn) asnMap
        | Some _ -> error (sprintf "Duplicate router name '%s' in topology" n.Name)
        let typ = 
            match n.Internal, n.CanOriginate with 
            | true, false -> Inside
            | true, true -> InsideOriginates
            | false, true
            | false, false -> Outside
        if n.Internal then 
            internalNames <- Set.add n.Name internalNames
        else 
            externalNames <- Set.add n.Name externalNames
        // TODO: duplicate names not handled
        let asn = string n.Asn
        let state = Node(asn, typ)
        nodeMap <- Map.add n.Name state nodeMap
        ignore (g.AddVertex state)

    for e in topo.Edges do 
        if not (nodeMap.ContainsKey e.Source) then 
            error (sprintf "Invalid edge source location %s in topology" e.Source)
        elif not (nodeMap.ContainsKey e.Target) then 
            error (sprintf "Invalid edge target location %s in topology" e.Target)
        else
            let x = nodeMap.[e.Source]
            let y = nodeMap.[e.Target]
            if e.Directed then
                addEdge x y
            else 
                addEdge x y 
                addEdge y x
    {Graph = g; 
     AsnMap = asnMap; 
     InternalNames = internalNames; 
     ExternalNames = externalNames; 
     AllNames = Set.union internalNames externalNames }


module Examples = 

    let topoDisconnected () = 
        let g = BidirectionalGraph<Node ,Edge<Node>>()
        let vA = Node("A", Inside)
        let vB = Node("B", Inside)
        let vC = Node("C", Inside)
        let vD = Node("D", Inside)
        addVertices g [vA; vB; vC; vD]
        addEdgesUndirected g [(vA,vB); (vC,vD)]
        g

    let topoDiamond () = 
        let g = BidirectionalGraph<Node ,Edge<Node>>()
        let vA = Node("A", InsideOriginates)
        let vX = Node("X", Inside)
        let vM = Node("M", Inside)
        let vN = Node("N", Inside)
        let vY = Node("Y", Inside)
        let vZ = Node("Z", Inside)
        let vB = Node("B", InsideOriginates)
        addVertices g [vA; vX; vM; vN; vY; vZ; vB]
        addEdgesUndirected g [(vA,vX); (vA,vM); (vM,vN); (vX,vN); (vN,vY); (vN,vZ); (vY,vB); (vZ,vB)]
        g

    let topoDatacenterSmall () = 
        let g = BidirectionalGraph<Node ,Edge<Node>>()
        let vA = Node("A", InsideOriginates)
        let vB = Node("B", InsideOriginates)
        let vC = Node("C", InsideOriginates)
        let vD = Node("D", InsideOriginates)
        let vX = Node("X", Inside)
        let vY = Node("Y", Inside)
        let vM = Node("M", Inside)
        let vN = Node("N", Inside)
        addVertices g [vA; vB; vC; vD; vX; vY; vM; vN]
        addEdgesUndirected g [(vA,vX); (vB,vX); (vC,vY); (vD,vY); (vX,vM); (vX,vN); (vY,vM); (vY,vN)]
        g

    let topoDatacenterMedium () = 
        let g = BidirectionalGraph<Node ,Edge<Node>>()
        let vA = Node("A", InsideOriginates)
        let vB = Node("B", InsideOriginates)
        let vC = Node("C", Inside)
        let vD = Node("D", Inside)
        let vE = Node("E", InsideOriginates)
        let vF = Node("F", InsideOriginates)
        let vG = Node("G", Inside)
        let vH = Node("H", Inside)
        let vX = Node("X", Inside)
        let vY = Node("Y", Inside)
        addVertices g [vA; vB; vC; vD; vE; vF; vG; vH; vX; vY]
        addEdgesUndirected g 
            [(vA,vC); (vA,vD); (vB,vC); (vB,vD); (vE,vG); (vE,vH); (vF,vG); (vF,vH); 
             (vC,vX); (vC,vY); (vD,vX); (vD,vY); (vG,vX); (vG,vY); (vH,vX); (vH,vY)]
        g

    let topoDatacenterMediumAggregation () = 
        let g = BidirectionalGraph<Node ,Edge<Node>>()
        let vA = Node("A", InsideOriginates)
        let vB = Node("B", InsideOriginates)
        let vC = Node("C", Inside)
        let vD = Node("D", Inside)
        let vE = Node("E", InsideOriginates)
        let vF = Node("F", InsideOriginates)
        let vG = Node("G", Inside)
        let vH = Node("H", Inside)
        let vX = Node("X", Inside)
        let vY = Node("Y", Inside)
        let vPeer = Node("PEER", Outside)
        addVertices g [vA; vB; vC; vD; vE; vF; vG; vH; vX; vY; vPeer]
        addEdgesUndirected g 
            [(vA,vC); (vA,vD); (vB,vC); (vB,vD); (vE,vG); (vE,vH); (vF,vG); (vF,vH); 
             (vC,vX); (vC,vY); (vD,vX); (vD,vY); (vG,vX); (vG,vY); (vH,vX); (vH,vY);
             (vX, vPeer); (vY, vPeer)]
        g

    let topoDatacenterLarge () = 
        let g = BidirectionalGraph<Node ,Edge<Node>>()
        let vA = Node("A", InsideOriginates)
        let vB = Node("B", InsideOriginates)
        let vC = Node("C", InsideOriginates)
        let vD = Node("D", InsideOriginates)
        let vE = Node("E", InsideOriginates)
        let vF = Node("F", InsideOriginates)
        let vM = Node("M", Inside)
        let vN = Node("N", Inside)
        let vO = Node("O", Inside)
        let vX = Node("X", Inside)
        let vY = Node("Y", Inside)
        let vZ = Node("Z", Inside)
        addVertices g [vA; vB; vC; vD; vE; vF; vM; vN; vO; vX; vY; vZ]
        addEdgesUndirected g 
            [(vA, vX); (vB, vX); (vC, vY); (vD, vY); (vE, vZ); (vF, vZ); 
             (vX, vM); (vX, vN); (vX, vO); (vY, vM); (vY, vN); (vY, vO);
             (vZ, vM); (vZ, vN); (vZ, vO)]
        g

    let topoBadGadget () = 
        let g = BidirectionalGraph<Node ,Edge<Node>>()
        let vA = Node("A", InsideOriginates)
        let vB = Node("B", InsideOriginates)
        let vC = Node("C", InsideOriginates)
        let vD = Node("D", InsideOriginates)
        addVertices g [vA; vB; vC; vD]
        addEdgesUndirected g [(vA,vB); (vB,vC); (vC,vA); (vA,vD); (vB,vD); (vC,vD)]
        g

    let topoBrokenTriangle () = 
        let g = BidirectionalGraph<Node ,Edge<Node>>()
        let vA = Node("A", InsideOriginates)
        let vB = Node("B", Inside)
        let vC = Node("C", InsideOriginates)
        let vD = Node("D", InsideOriginates)
        let vE = Node("E", Inside)
        addVertices g [vA; vB; vC; vD; vE]
        addEdgesUndirected g [(vC,vA); (vA,vE); (vA,vB); (vE,vD); (vD,vB)]
        g

    let topoBigDipper () = 
        let g = BidirectionalGraph<Node ,Edge<Node>>()
        let vA = Node("A", InsideOriginates)
        let vC = Node("C", InsideOriginates)
        let vD = Node("D", InsideOriginates)
        let vE = Node("E", Inside)
        addVertices g [vA; vC; vD; vE]
        addEdgesUndirected g [(vC,vA); (vA,vE); (vA,vD); (vE,vD)]
        g

    let topoSeesaw () = 
        let g = BidirectionalGraph<Node ,Edge<Node>>()
        let vM = Node("M", InsideOriginates)
        let vN = Node("N", Inside)
        let vO = Node("O", Inside)
        let vX = Node("X", InsideOriginates)
        let vA = Node("A", InsideOriginates)
        let vB = Node("B", InsideOriginates)
        addVertices g [vM; vN; vO; vX; vA; vB]
        addEdgesUndirected g [(vM, vN); (vM, vO); (vO, vX); (vN, vX); (vX, vA); (vX, vB)]
        g

    let topoStretchingManWAN () = 
        let g = BidirectionalGraph<Node ,Edge<Node>>()
        let vA = Node("A", Inside)
        let vB = Node("B", Inside)
        let vC = Node("C", Inside)
        let vD = Node("D", Inside)
        let vX = Node("X", Outside)
        let vY = Node("Y", Outside)
        let vZ = Node("Z", Outside)
        addVertices g [vA; vB; vC; vD; vX; vY; vZ]
        addEdgesUndirected g [(vX, vA); (vX, vB); (vA, vC); (vB, vC); (vC, vD); (vD, vY); (vD, vZ)]
        g

    let topoStretchingManWAN2 () = 
        let g = BidirectionalGraph<Node ,Edge<Node>>()
        let vA = Node("A", Inside)
        let vB = Node("B", Inside)
        let vC = Node("C", Inside)
        let vD = Node("D", Inside)
        let vE = Node("E", Inside)
        let vW = Node("W", Outside)
        let vX = Node("X", Outside)
        let vY = Node("Y", Outside)
        let vZ = Node("Z", Outside)
        addVertices g [vA; vB; vC; vD; vE; vW; vX; vY; vZ]
        addEdgesUndirected g 
            [(vW,vA); (vW,vB); (vA,vC); (vB,vC); (vC, vD); 
             (vC, vE); (vD, vX); (vD, vY); (vE, vZ)]
        g

    let topoPinCushionWAN () = 
        let g = BidirectionalGraph<Node ,Edge<Node>>()
        let vA = Node("A", Inside)
        let vB = Node("B", Inside)
        let vC = Node("C", Inside)
        let vD = Node("D", Inside)
        let vE = Node("E", Inside)
        let vW = Node("W", Outside)
        let vX = Node("X", Outside)
        let vY = Node("Y", Outside)
        let vZ = Node("Z", Outside)
        addVertices g [vA; vB; vC; vD; vE; vW; vX; vY; vZ]
        addEdgesUndirected g [(vW, vA); (vX, vB); (vA, vC); (vB, vC); (vC, vD); (vC, vE); (vD, vY); (vE, vZ)]
        g

    let topoBackboneWAN () = 
        let g = BidirectionalGraph<Node ,Edge<Node>>()
        let vA = Node("A", InsideOriginates)
        let vSEA = Node("SEA", InsideOriginates)
        let vNY = Node("NY", InsideOriginates)
        let vX = Node("X", Outside)
        let vY = Node("Y", Outside)
        addVertices g [vA; vSEA; vNY; vX; vY]
        addEdgesUndirected g [(vA, vSEA); (vA, vNY); (vSEA, vX); (vNY, vY)]
        g

    /// Fattree topology 

    type Tiers = Dictionary<Node,int>
    type Prefixes = Dictionary<Node,Prefix.T>

    let getPrefix i = 
        let a = uint32 (i / (256 * 256))
        let b = uint32 (i / 256)
        let c = uint32 (i % 256)
        Prefix.prefix (a, b, c, 0u) 24u

    let fatTree k : T * Prefixes * Tiers = 
        let iT0 = (k * k) / 2
        let iT1 = (k * k) / 2
        let iT2 = (k * k) / 4
        let g = BidirectionalGraph<Node ,Edge<Node>>()
        let prefixes = Dictionary()
        let tiers = Dictionary()
        let routersT0 = Array.init iT0 (fun i ->
            let name = "T0_" + string i
            let v = Node(name, InsideOriginates)
            ignore (g.AddVertex v)
            prefixes.[v] <- getPrefix i
            tiers.[v] <- 0
            v)
        let routersT1 = Array.init iT1 (fun i -> 
            let name = "T1_" + string i
            let v = Node(name, Inside)
            ignore (g.AddVertex v)
            tiers.[v] <- 1
            v)
        let routersT2 = Array.init iT2 (fun i ->
            let name = "T2_" + string i
            let v = Node(name, Inside)
            ignore (g.AddVertex v)
            tiers.[v] <- 2
            v)
        let perPod = (k/2)
        for i = 0 to  iT0-1 do
            let pod = i / (perPod)
            for j = 0 to perPod-1 do
                let x = routersT0.[i]
                let y = routersT1.[pod*perPod + j]
                addEdgesUndirected g [(x,y)]
        for i = 0 to iT1-1 do 
            for j = 0 to perPod-1 do
                let rem = i % perPod
                let x = routersT1.[i]
                let y = routersT2.[rem*perPod + j]
                addEdgesUndirected g [(x,y)]
        let back1 = Node("BACK1", Outside)
        let back2 = Node("BACK2", Outside)
        ignore (g.AddVertex back1)
        ignore (g.AddVertex back2)
        for i = 0 to iT2-1 do 
            let x = routersT2.[i]
            addEdgesUndirected g [(x,back1); (x,back2)]
        (g, prefixes, tiers)

    let complete n  = 
        let g = BidirectionalGraph<Node ,Edge<Node>>()
         // setup external peers
        let externalPeers = Dictionary()
        let internalPeers =  Dictionary()
        // setup internal full mesh
        for i = 0 to n-1 do
            let name = "R" + string i
            let v = Node(name, Inside)
            ignore (g.AddVertex v)
            internalPeers.[name] <- v
        for v1 in g.Vertices do 
            for v2 in g.Vertices do 
                if isInside v1 && isInside v2 && v1 <> v2 then
                    let e = Edge(v1,v2)
                    ignore (g.AddEdge e)
        // add connections to external peers
        for kv in internalPeers do
            let name = kv.Key 
            let router = kv.Value
            // add dcs
            for i = 0 to 9 do
                let eName = "Cust" + string i + name
                let ePeer = Node(eName, Outside)
                ignore (g.AddVertex ePeer)
                let e1 = Edge (router, ePeer) 
                let e2 = Edge (ePeer, router) 
                ignore (g.AddEdge e1)
                ignore (g.AddEdge e2)
            // add peers
            for i = 0 to 19 do 
                let eName = "Peer" + string i + name
                let ePeer = Node(eName, Outside)
                ignore (g.AddVertex ePeer)
                let e1 = Edge (router, ePeer) 
                let e2 = Edge (ePeer, router) 
                ignore (g.AddEdge e1)
                ignore (g.AddEdge e2)
            // add paid on net
            for i = 0 to 19 do 
                let eName = "OnPaid" + string i + name
                let ePeer = Node(eName, Outside)
                ignore (g.AddVertex ePeer)
                let e1 = Edge (router, ePeer) 
                let e2 = Edge (ePeer, router) 
                ignore (g.AddEdge e1)
                ignore (g.AddEdge e2)
            // add paid off net
            for i = 0 to 19 do 
                let eName = "OffPaid" + string i + name
                let ePeer = Node(eName, Outside)
                ignore (g.AddVertex ePeer)
                let e1 = Edge (router, ePeer) 
                let e2 = Edge (ePeer, router) 
                ignore (g.AddEdge e1)
                ignore (g.AddEdge e2)
        g


module Test = 

    let testTopologyWellFormedness () =
        printf "Topology well-formedness "
        let topo = Examples.topoDisconnected ()
        if isWellFormed topo then failed ()
        else passed ()

    let run () = 
        testTopologyWellFormedness ()