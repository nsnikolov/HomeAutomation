import { HubConnectionBuilder, HttpTransportType, LogLevel } from '@microsoft/signalr'
import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'

export function useLiveUpdates(propertyId: number) {
  const client = useQueryClient()
  const [connectedProperty, setConnectedProperty] = useState<number | null>(null)

  useEffect(() => {
    let disposed = false
    let retry: ReturnType<typeof setTimeout> | undefined
    let ewelinkRefresh: ReturnType<typeof setTimeout> | undefined
    let ewelinkFetching = false
    let ewelinkDirty = false
    let attempts = 0
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/monitoring', { transport: HttpTransportType.WebSockets | HttpTransportType.LongPolling })
      .configureLogging(LogLevel.None)
      .withAutomaticReconnect()
      .build()
    const invalidate = () => {
      if (!disposed) void client.invalidateQueries({ queryKey: ['property', propertyId] })
    }
    const disconnected = () => {
      if (!disposed) setConnectedProperty(null)
    }
    const retryStart = () => {
      if (!disposed) retry = setTimeout(() => { void start() }, Math.min(2000 * 2 ** attempts++, 30000))
    }
    const subscribe = async () => {
      try {
        await connection.invoke('Subscribe', propertyId)
        if (!disposed) setConnectedProperty(propertyId)
        attempts = 0
        invalidate()
      } catch {
        disconnected()
        await connection.stop().catch(() => undefined)
      }
    }
    async function start() {
      try {
        await connection.start()
        if (disposed) await connection.stop()
        else await subscribe()
      } catch {
        disconnected()
        retryStart()
      }
    }

    connection.on('stateChanged', (event: { propertyId: number }) => {
      if (event.propertyId === propertyId) invalidate()
    })
    const scheduleEwelinkRefresh = () => {
      if (disposed || ewelinkFetching || !ewelinkDirty || ewelinkRefresh !== undefined) return
      ewelinkRefresh = setTimeout(() => {
        ewelinkRefresh = undefined
        if (disposed) return
        ewelinkDirty = false
        ewelinkFetching = true
        void client.invalidateQueries({ queryKey: ['property', propertyId, 'ewelink'], exact: true })
          .finally(() => {
            ewelinkFetching = false
            scheduleEwelinkRefresh()
          })
      }, 250)
    }
    connection.on('ewelinkChanged', (event: { propertyId: number }) => {
      if (event.propertyId !== propertyId) return
      ewelinkDirty = true
      scheduleEwelinkRefresh()
    })
    connection.onreconnecting(disconnected)
    connection.onreconnected(subscribe)
    connection.onclose(() => { disconnected(); retryStart() })
    void start()

    return () => {
      disposed = true
      clearTimeout(retry)
      clearTimeout(ewelinkRefresh)
      connection.off('stateChanged')
      connection.off('ewelinkChanged')
      void connection.stop().catch(() => undefined)
    }
  }, [client, propertyId])

  return connectedProperty === propertyId
}
