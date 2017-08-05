/// The registry is the composition root of Logary
namespace Logary

open Hopac
open Hopac.Infixes
open NodaTime
open System
open System.IO
open Logary.Message
open Logary.Target
open Logary.Internals

module internal GlobalService =
  open Global

  let create (t : T) (ilogger : Logger) =
    let logger = ilogger |> Logger.apply (setSimpleName "Logary.Globals")
    let pauseCh, resumeCh, shutdownCh = Ch (), Ch (), Ch ()

    let rec init () =
      let prev = !config
      initialise t
      running t (fst prev)

    and running myself prev =
      Alt.choose [
        pauseCh ^=> fun (ack, nack) ->
          logger.debug (eventX "Pausing.")
          initialise prev
          ack *<= () >>=. running myself prev

        resumeCh ^=> fun (ack, nack) ->
          logger.debug (eventX "Resuming.")
          initialise myself
          ack *<= () >>=. running myself prev

        shutdownCh ^=> fun ack ->
          logger.debug (eventX "Shutting down.")
          initialise prev
          ack *<= ()
      ]

    let shutdown : Alt<Promise<unit>> =
      shutdownCh *<-=>= fun repl -> repl
      |> Alt.afterFun (fun iv -> iv :> _)

    let loop = Job.supervise logger Policy.terminate (init ())
    Service.create logger "globals" pauseCh resumeCh shutdown loop

  (* // cc: @oskarkarlsson ;)
  let scoped (globals : Service<Service.T>) (logger : Logger) =
    globals |> Service.pause >>-.
    { new IAsynDisposable with
        member x.AsyncDispose() =
          globals |> Service.resume
    }
  *)

module Engine =

  type T =
    private {
      subscriptions : HashMap<string, Message -> Job<unit>>
      inputCh : Ch<LogLevel * (LogLevel -> Message)>
      shutdownCh : Ch<unit>
      subscriberCh : Ch<string * (Message->Job<unit>)> 
    }

  let create (Processing processing) : Job<T> =
    let inputCh, emitCh, shutdownCh, subscriberCh = Ch (), Ch (), Ch (), Ch ()

    let engine = { subscriptions = HashMap.empty
                   inputCh = inputCh
                   shutdownCh = shutdownCh
                   subscriberCh = subscriberCh
                 }

    let rec loop (subsribers:HashMap<string, Message -> Job<unit>>) =
      Alt.choose [
        inputCh ^=> fun (level, messageFactory) -> 
          processing (messageFactory level) emitCh
          ^=>. loop subsribers

        emitCh ^=> fun message ->
          // send to targets
          let targetName = Message.tryGetContext "target" message
          match targetName with 
          | Some (String targetName) ->
              let subscriber = HashMap.tryFind targetName subsribers 
              match subscriber with
              | None -> loop subsribers
              | Some subscriber -> 
                  Alt.prepareJob (fun () -> ((subscriber message) >>-. loop subsribers))
          | _ -> loop subsribers
          
        subscriberCh ^=> fun (key, sink) ->
          subsribers
          |> HashMap.add key sink 
          |> loop

        upcast shutdownCh
      ]

    Job.start (loop engine.subscriptions)
    >>-. engine

  let subscribe (engine : T) (key:string) (sink : Message -> Job<unit>) : Job<unit> =
    engine.subscriberCh *<- (key, sink)
    :> Job<unit>

  let unsubscribe key (sink : Message -> Job<unit>) : Job<unit> =
    Job.result ()

  let pause (engine : T) : Alt<unit> =
    Alt.always ()

  let resume (engine : T) : Alt<unit> =
    Alt.always ()

  let shutdown (engine : T) =
    engine.shutdownCh *<- ()

  let log (engine : T) (logLevel : LogLevel) (messageFactory : LogLevel -> Message) : Alt<unit> =
    Alt.always ()

  let logWithAck (engine : T) (logLevel : LogLevel) (messageFactory : LogLevel -> Message) : Alt<Promise<unit>> =
    engine.inputCh *<- (logLevel, messageFactory) 
    ^->. Promise (()) 


/// When you validate the configuration, you get one of these.
///
/// This is the logary configuration structure having a memory of all
/// configured targets, metrics, healthchecks, middlewares, etc.
type LogaryConf =
  /// A map of the targets by name.
  abstract targets : HashMap<string, TargetConf>
  /// A map of metrics by name.
  abstract metrics : HashMap<string, MetricConf>
  /// A map of health checks by name.
  abstract healthChecks : HashMap<string, HealthCheckConf>
  /// Service metadata - what name etc.
  abstract runtimeInfo : RuntimeInfo
  /// Extra middleware added to every resolved logger.
  abstract middleware : Middleware[]
  /// Optional stream transformer.
  abstract processing : Processing

/// A data-structure that gives information about the outcome of a flush
/// operation on the Registry. This data structure is only relevant if the
/// flush operation had an associated timeout.
type FlushInfo = FlushInfo of acks:string list * timeouts:string list

/// A data-structure that gives information about the outcome of a shutdown
/// operation on the Registry. This data structure is only relevant if the
/// shutdown operation had an associated timeout.
type ShutdownInfo = ShutdownInfo of acks:string list * timeouts:string list

/// LogManager is the public interface to Logary and takes care of getting
/// loggers from names. It is also responsible for running Dispose at the
/// end of the application in order to run the target shutdown logic. That said,
/// the body of the software should be crash only, so even if you don't call dispose
/// terminating the application, it should continue working.
///
/// This is also a synchronous wrapper around the asynchronous actors that make
/// up logary
type LogManager =
  /// Gets the service name that is used to filter and process the logs further
  /// downstream. This property is configured at initialisation of Logary.
  abstract runtimeInfo : RuntimeInfo

  /// Get a logger denoted by the name passed as the parameter. This name can either be
  /// a specific name that you keep for a sub-component of your application or
  /// the name of the class. Also have a look at Logging.GetCurrentLogger().
  abstract getLogger : PointName -> Job<Logger>

  /// Awaits that all targets finish responding to a flush message
  /// so that we can be certain they have processed all previous messages.
  /// This function is useful together with unit tests for the targets.
  abstract flushPending : Duration -> Alt<FlushInfo>

  /// Shuts Logary down after flushing, given a timeout duration to wait before
  /// counting the target as timed out in responding. The duration is applied
  /// to each actor's communication. Does an ordered shutdown.
  ///
  /// First duration: flush duration
  /// Second duration: shutdown duration
  /// Returns the shutdown book keeping info
  abstract shutdown : flush:Duration -> shutdown:Duration -> Alt<FlushInfo * ShutdownInfo>

/// This is the main state container in Logary.
module Registry =
  /// The holder for the channels of communicating with the registry.
  type T =
    private {
      runtimeInfo : RuntimeInfo

      engine      : Engine.T

      /// Get a logger for the given point name (the path of the logger). This
      /// operation should not fail, so there's no nack promise passed.
      getLoggerCh : Ch<PointName * Middleware option * IVar<Logger>>

      /// Flush all pending messages from the registry to await shutdown and
      /// ack on the `ackCh` when done. If the client nacks the request, the
      /// `nack` promise is filled with a unit value. Optional duration of how
      /// long the flush 'waits' for targets before returning a FlushInfo.
      flushCh : Ch<Ch<FlushInfo> * Promise<unit> * Duration option>

      /// Shutdown the registry in full. This operation cannot be cancelled and
      /// so the caller is promised a ShutdownInfo.
      shutdownCh : Ch<IVar<ShutdownInfo> * Duration option>
    }

  /// Gets a logger from the registry, by name. This will always return a
  /// `Logger` value.
  let getLogger (t : T) name : Job<Logger> =
    t.getLoggerCh *<+=>- fun resCh -> name, None, resCh
    :> Job<_>

  /// Gets a logger from the registry, by name. This will always return a
  /// job with a `Logger` value.
  let getLoggerT (t : T) name : Logger =
    getLogger t name |> PromisedLogger.create name

  /// Gets a logger from the registry, by name, with attached middleware. This
  /// will always return a job with a `Logger` value.
  let getLoggerWithMiddleware (t : T) (name : PointName) (middleware : Middleware) : Job<Logger> =
    t.getLoggerCh *<+=>- fun resCh -> name, Some middleware, resCh
    :> Job<_>

  /// Gets a logger from the registry, by name, with attached middleware. This
  /// will always return a `Logger` value.
  let getLoggerWithMiddlewareT (t : T) name middleware : Logger =
    getLoggerWithMiddleware t name middleware |> PromisedLogger.create name

  /// Flush all pending messages for all targets. Flushes with no timeout; if
  /// this Alternative yields, all targets were flushed.
  let flush (t : T) : Alt<unit> =
    t.flushCh *<+->- fun flushCh nack -> flushCh, nack, None
    |> Alt.afterFun (fun _ -> ())

  /// Flush all pending messages for all targets. This Alternative always
  /// yields after waiting for the specified `timeout`; then giving back the
  /// `FlushInfo` data-structure that recorded what targets were successfully
  /// flushed and which ones timed out.
  let flushWithTimeout (t : T) (timeout : Duration) : Alt<FlushInfo> =
    t.flushCh *<+->- fun flushCh nack -> flushCh, nack, Some timeout

  /// Shutdown the registry and flush all targets before shutting it down. This
  /// function does not specify a timeout, neither for the flush nor for the
  /// shutting down of targets, and so it does not return a `ShutdownInfo`
  /// data-structure.
  let shutdown (t : T) : Alt<ShutdownInfo> =
    t.shutdownCh *<-=>- fun shutdownCh -> shutdownCh, None

  /// Shutdown the registry and flush all targets before shutting it down. This
  /// function specifies both a timeout for the flushing of targets and the
  /// shutting down of the registry. The Alternative yields after a maximum of
  /// `shutdownTimeout` + `flushTimeout`, with information about the shutdown.
  let shutdownWithTimeouts (t : T) (flushTimeout : Duration) (shutdownTimeout : Duration) : Alt<FlushInfo * ShutdownInfo> =
    flushWithTimeout t flushTimeout ^=> fun flushInfo ->
    t.shutdownCh *<-=>- fun shutdownCh -> shutdownCh, Some shutdownTimeout
    |> Alt.afterFun (fun shutdownInfo -> flushInfo, shutdownInfo)

  let runtimeInfo (t : T) : RuntimeInfo =
    t.runtimeInfo

  module internal Impl =

    let createLogger engine name mid =
      let logger =
        { new Logger with
            member x.name = name
            member x.level = Verbose // TOOD: ship back from engine?
            member x.log level messageFactory =
              Engine.log engine level messageFactory
            member x.logWithAck level messageFactory =
              Engine.logWithAck engine level messageFactory
        }

      Logger.apply mid logger

    let createGlobals (conf : LogaryConf) (x : T) =
      let config =
        { Global.defaultConfig with
            getLogger = getLoggerT x
            getLoggerWithMiddleware = getLoggerWithMiddlewareT x }
      GlobalService.create config conf.runtimeInfo.logger

    let spawn kind (ri : RuntimeInfo) mapping factory =
      let creator (KeyValue (name, conf)) =
        let mname = PointName [| "Logary"; sprintf "%s(%s)" kind name |]
        let logger = ri.logger |> Logger.apply (setName mname)
        let ri = ri |> RuntimeInfo.setLogger logger
        (*conf :> Service, *)
        factory ri conf

      mapping
      |> List.ofSeq
      |> List.traverseJobA creator
      //|> List.traverseJobA supervise

    let pollServices (services : Service<Service.T> list) : Alt<(Service<Service.T> * exn) list> =
      let faulted = IVar ()
      
      let request =
        services
        |> List.traverseJobA (fun s -> Service.getState s >>- fun state -> s, state)
        |> Job.map (List.choose (function
            | service, Faulted e -> Some (service, e)
            | _ -> None))
        |> Job.bind (function
            | [] -> Job.result ()
            | res -> faulted *<= res)
        |> Job.start

      Alt.prepareJob (fun () -> request >>-. faulted)

  open Impl

  // Middleware at:
  //  - LogaryConf (goes on all loggers) (composes here)
  //  - TargetConf (goes on specific target) (composes in engine)
  //  - individual loggers (composes at call-site, or in #create methods of services)

  let create (conf : LogaryConf) : Job<T> =
    let ri, rname, rmid =
      conf.runtimeInfo,
      PointName [| "Logary"; "Registry" |],
      List.ofArray conf.middleware
    let rlogger = conf.runtimeInfo.logger |> Logger.apply (setName rname)
    spawn "Target" ri conf.targets Target.create >>= fun (targets : Target.T list) ->
    spawn "Metric" ri conf.metrics Metric.create >>= fun (metrics : Metric.T list) ->
    spawn "HealthCheck" ri conf.healthChecks HealthCheck.create >>= fun (hcs : HealthCheck.T list) ->
    Engine.create conf.processing >>= fun engine ->

    let getLoggerCh, flushCh, shutdownCh = Ch (), Ch (), Ch ()

    let rec initialise () =
      // let targetSvc (name, target) =
      //   Service.createSimple ri.logger name (Target.shutdown target)
      // targets |> List.map (fun (name, target) -> >>= fun targets ->
      // supervise metrics >>= fun metrics ->
      // supervise hcs >>= fun hcs ->
      // running (targets, metrics, hcs)
      running []

    and running (services : Service<Service.T> list) =
      Alt.choose [
        pollServices services ^=> fun _ ->
          faulted ()

        getLoggerCh ^=> fun (name, lmid, repl) ->
          let cmid = Middleware.compose (lmid |> Option.fold (fun s t -> t :: s) rmid)
          repl *<= createLogger engine name cmid >>=. running services

        flushCh ^=> fun (ackCh, nack, timeout) ->
          // let flush = Engine.flush ... ... FlushInfo
          // let nack ... FlushInfo
          // let timeout = timeout ... FlushInfo
          // (Alt.choosy [| flush; timeout; nack |] ^=> running
          running services

        shutdownCh ^=> fun (res, timeout) ->
          // TODO: handle shutting down metrics, hcs and targets
          rlogger.infoWithAck (eventX "Shutting down")
      ]

    and faulted () =
      Alt.always ()

    let state =
      { runtimeInfo = ri
        engine = engine
        getLoggerCh = getLoggerCh
        flushCh = flushCh
        shutdownCh = shutdownCh }

    let globals = createGlobals conf state
    Job.supervise rlogger (Policy.restartDelayed 500u) (initialise ()) >>-.
    state

  let toLogManager (t : T) : LogManager =
    { new LogManager with
        member x.getLogger name =
          getLogger t name
        member x.runtimeInfo =
          t.runtimeInfo
        member x.flushPending dur =
          flushWithTimeout t dur
        member x.shutdown flushTO shutdownTO =
          shutdownWithTimeouts t flushTO shutdownTO
    }

[<AutoOpen>]
module LogManagerEx =

  type LogManager with
    /// Get a logger denoted by the name passed as the parameter. This name can either be
    /// a specific name that you keep for a sub-component of your application or
    /// the name of the class. Also have a look at Logging.GetCurrentLogger().
    member x.getLoggerT name : Logger =
      x.getLogger name
      |> PromisedLogger.create name