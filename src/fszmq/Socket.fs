﻿(*-------------------------------------------------------------------------
Copyright (c) Paulmichael Blasucci.                                        
                                                                           
This source code is subject to terms and conditions of the Apache License, 
Version 2.0. A copy of the license can be found in the License.html file   
at the root of this distribution.                                          
                                                                           
By using this source code in any fashion, you are agreeing to be bound     
by the terms of the Apache License, Version 2.0.                           
                                                                           
You must not remove this notice, or any other, from this software.         
-------------------------------------------------------------------------*)
namespace fszmq

open System
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Text

/// Encapsulates data generated by various ZMQ monitoring events
type ZMQEvent = 
  { EventID : uint16
    Address : string
    Details : ZMQEventDetails }
  with
    /// Constructs a ZMQEvent option from a raw (binary) message
    static member TryBuild(message) =
      match message with
      | [| details; address; |] ->
        match Array.length details with
        | n when n = ZMQ.EVENT_DETAIL_SIZE ->
            let event = BitConverter.ToUInt16(details,0)
            let value = BitConverter.ToInt32 (details,sizeof<uint16>)
            { EventID = event; 
              Address = Encoding.UTF8.GetString(address); 
              Details = ZMQEventDetails.Build(event,value) }
            |> Some
        | _ -> None
      | _   -> None
    /// Constructs a ZMQEvent from a raw (binary) message; 
    /// will raise NotAnEvent exception if message format is incorrect
    static member Build(message) =
      match ZMQEvent.TryBuild(message) with
      | Some zmqEvent -> zmqEvent
      | None          -> raise <| ZMQ.NotAnEvent message
and ZMQEventDetails =
  | Connected       of handle   : int
  | ConnectDelayed
  | ConnectRetried  of interval : int
  | Listening       of handle   : int
  | BindFailed      of error    : ZMQError
  | Accepted        of handle   : int
  | AcceptFailed    of error    : ZMQError
  | Closed          of handle   : int
  | CloseFailed     of error    : ZMQError
  | Disconnected    of handle   : int
  | MonitorStopped
  | Unknown
  with
    /// Constructs a ZMQEventDetails instance based on a (native) ZeroMQ event and associated data
    static member Build(event,data) =
      match event with
      // data is a reconnect interval
      | ZMQ.EVENT_CONNECT_RETRIED -> ConnectRetried data
      // data is a socket handle (a.k.a. file descriptor, or fd)
      | ZMQ.EVENT_LISTENING       -> Listening    data
      | ZMQ.EVENT_CONNECTED       -> Connected    data
      | ZMQ.EVENT_ACCEPTED        -> Accepted     data
      | ZMQ.EVENT_CLOSED          -> Closed       data
      | ZMQ.EVENT_DISCONNECTED    -> Disconnected data
      // data is a ZeroMQ error number (for use with ZMQError)
      | ZMQ.EVENT_BIND_FAILED     -> BindFailed   (ZMQ.buildError data)
      | ZMQ.EVENT_ACCEPT_FAILED   -> AcceptFailed (ZMQ.buildError data)
      | ZMQ.EVENT_CLOSE_FAILED    -> CloseFailed  (ZMQ.buildError data)
      // data is meaningless
      | ZMQ.EVENT_CONNECT_DELAYED -> ConnectDelayed
      | ZMQ.EVENT_MONITOR_STOPPED -> MonitorStopped
      | _                         -> Unknown

/// Contains methods for working with Socket instances
[<Extension;CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Socket =

(* connectivity *)

  /// Causes an endpoint to start accepting
  /// connections at the given address
  [<Extension;CompiledName("Bind")>]
  let bind (socket:Socket) address =
    if C.zmq_bind(socket.Handle,address) <> 0 then ZMQ.error()

  /// Causes an endpoint to stop accepting
  /// connections at the given address
  [<Extension;CompiledName("Unbind")>]
  let unbind (socket:Socket) address =
    if C.zmq_unbind(socket.Handle,address) <> 0 then ZMQ.error()

  /// Connects to an endpoint to the given address
  [<Extension;CompiledName("Connect")>]
  let connect (socket:Socket) address =
    if C.zmq_connect(socket.Handle,address) <> 0 then ZMQ.error()

  /// Disconnects to an endpoint from the given address
  [<Extension;CompiledName("Disconnect")>]
  let disconnect (socket:Socket) address =
    if C.zmq_disconnect(socket.Handle,address) <> 0 then ZMQ.error()

(* socket options *)

  /// Gets the value of the given option for the given Socket
  [<Extension;CompiledName("GetOption")>]
  let getOption<'t> (socket:Socket) socketOption : 't =
    let size,read =
      let   t = typeof<'t>
      if    t = typeof<int>     then   4,(snd >> readInt32  >> box)
      elif  t = typeof<bool>    then   4,(snd >> readBool   >> box)
      elif  t = typeof<int64>   then   8,(snd >> readInt64  >> box)
      elif  t = typeof<uint64>  then   8,(snd >> readUInt64 >> box)
      elif  t = typeof<string>  then 255,(       readString >> box)
      elif  t = typeof<byte[]>  then 255,(       readBytes  >> box)
                                else invalidOp "Invalid data type"
    let buffer = Marshal.AllocHGlobal(size)
    try
      let mutable size' = unativeint size
      if C.zmq_getsockopt(socket.Handle,socketOption,buffer,&size') <> 0 then ZMQ.error()
      downcast read (size',buffer)
    finally
      Marshal.FreeHGlobal(buffer)

  /// Sets the given option value for the given Socket
  [<Extension;CompiledName("SetOption")>]
  let setOption (socket:Socket) (socketOption,value:'t) =
    let size,write =
      match box value with
      | :? (int32 ) as v  -> sizeof<Int32>,(writeInt32  v)     
      | :? (bool  ) as v  -> sizeof<Int32>,(writeBool   v)   
      | :? (int64 ) as v  -> sizeof<Int32>,(writeInt64  v)    
      | :? (uint64) as v  -> sizeof<Int64>,(writeUInt64 v)   
      | :? (string) as v  -> v.Length     ,(writeString v)
      | :? (byte[]) as v  -> v.Length     ,(writeBytes  v)
      | _                 -> invalidOp "Invalid data type"
    let buffer = Marshal.AllocHGlobal(size)
    try
      write(buffer)
      if C.zmq_setsockopt(socket.Handle,socketOption,buffer,unativeint size) <> 0 then ZMQ.error()
    finally
      Marshal.FreeHGlobal(buffer)

  /// Sets the given block of option values for the given Socket
  [<Extension;CompiledName("Configure")>]
  let config socket socketOptions =
    Seq.iter (fun (opt:int * obj) -> setOption socket opt) socketOptions
  
(* subscriptions *)

  /// Adds one subscription for each of the given topics
  [<Extension;CompiledName("Subscribe")>]
  let subscribe socket topics =
    Seq.iter (fun (t:byte[]) -> setOption socket (ZMQ.SUBSCRIBE,t)) topics

  /// Removes one subscription for each of the given topics
  [<Extension;CompiledName("Unsubscribe")>]
  let unsubscribe socket topics =
    Seq.iter (fun (t:byte[]) -> setOption socket (ZMQ.UNSUBSCRIBE,t)) topics

(* message sending *)
  
  /// Sends a frame, with the given flags, returning true (or false) 
  /// if the send was successful (or should be re-tried)
  [<Extension;CompiledName("TrySend")>]
  let trySend socket flags frame =
    use msg = new Message(frame)
    Message.trySend msg socket flags

  /// Sends a frame, indicating no more frames will follow
  [<Extension;CompiledName("Send")>]
  let send socket frame = 
    use msg = new Message(frame)
    Message.send msg socket
  
  /// Sends a frame, indicating more frames will follow, and returning the given socket
  [<Extension;CompiledName("SendMore")>]
  let sendMore socket frame : Socket = 
    use msg = new Message(frame)
    Message.sendMore msg socket
    socket
  
  /// Operator equivalent to Socket.send
  let (<<|) socket = send socket
  /// Operator equivalent to Socket.sendMore
  let (<~|) socket = sendMore socket

  /// Operator equivalent to Socket.send (with arguments reversed)
  let (|>>) data socket = socket <<| data
  /// Operator equivalent to Socket.sendMore (with arguments reversed)
  let (|~>) data socket = socket <~| data

  /// Sends all frames of a given message
  [<Extension;CompiledName("SendAll")>]
  let sendAll socket message =
    message
    |> Seq.take (Seq.length message - 1)
    |> Seq.fold sendMore socket
    |> (fun socket -> send socket (Seq.last message))

(* message receiving *)

  /// Gets the next available frame from a socket, returning a frame option
  /// where None indicates the operation should be re-attempted
  [<Extension;CompiledName("TryRecv")>]
  let tryRecv socket flags =
    Message.tryRecv socket flags 
    |> Option.map (fun msg -> let mutable frame' = Array.empty
                              frame' <- Message.data msg
                              (msg :> IDisposable).Dispose()
                              frame')
    
  /// Waits for (and returns) the next available frame from a socket
  [<Extension;CompiledName("Recv")>]
  let recv socket = Option.get (tryRecv socket ZMQ.WAIT)
  
  /// Returns true if more message frames are available
  [<Extension;CompiledName("RecvMore")>]
  let recvMore socket = getOption<bool> socket ZMQ.RCVMORE

  /// Retrieves all frames of the next available message
  [<Extension;CompiledName("RecvAll")>]
  let recvAll socket =
    [|  yield socket |> recv 
        while socket |> recvMore do yield socket |> recv  |]

(*
TODO: evaluate this commented-out block of code after closing issue #12 (https://github.com/pblasucci/fszmq/issues/12)
  
  /// Copies a message frame-wise from one socket to another without
  /// first marshaling the message part into the managed code space
  [<Extension;CompiledName("Transfer")>]
  let transfer (socket:Socket) (target:Socket) =
    use frame = new Message()
    let rec send' flags =
      match C.zmq_msg_send(frame.Handle,target.Handle,flags) with
      | Okay -> ((* pass *))
      | Busy -> send' flags
      | Fail -> ZMQ.error()
    let loop = ref true
    while !loop do
      match C.zmq_msg_recv(frame.Handle,socket.Handle,ZMQ.WAIT) with
      | Okay -> loop := socket |> recvMore
                send' (if !loop then ZMQ.SNDMORE else ZMQ.DONTWAIT)
      | _ -> ZMQ.error()

  /// Operator equivalent to Socket.transfer
  let (>|<) socket target = target |> transfer socket
*)

(* monitoring *)
  /// Creates a ZMQ.PAIR socket, bound to the given address, which broadcasts 
  /// events for the given socket. These events should be consumed by another ZMQ.PAIR socket 
  /// connected to the given address (preferably on a background thread). 
  [<Extension;CompiledName("CreateMonitor")>]
  let monitor (socket:Socket) address events =
    if C.zmq_socket_monitor(socket.Handle,address,events) < 0 then ZMQ.error()
