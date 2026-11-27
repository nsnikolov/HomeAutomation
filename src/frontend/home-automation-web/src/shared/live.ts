import { HubConnectionBuilder, HttpTransportType, LogLevel } from '@microsoft/signalr'
import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { refreshNotifications } from './alerts'

export function useLiveUpdates(propertyId: number) {
  const client = useQueryClient()
  const [connectedProperty, setConnectedProperty] = useState<number | null>(null)

  useEffect(() => {
    let disposed = false
    let retry: ReturnType<typeof setTimeout> | undefined
    let ewelinkRefresh: ReturnType<typeof setTimeout> | undefined
    let ewelinkFetching = false
    let ewelinkDirty = false
    let ewelinkTraceTimer: ReturnType<typeof setTimeout> | undefined
    let ewelinkNotices = 0
    let ewelinkElectricalNotices = 0
    let ewelinkWrongProperty = 0
    let ewelinkRefreshes = 0
    let ewelinkRefreshErrors = 0
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
    const invalidateDashboard = () => client.invalidateQueries({ queryKey: ['property', propertyId, 'dashboard'], exact: true })
    connection.on('dashboardChanged', (event: { propertyId: number }) => {
      if (event.propertyId === propertyId) void invalidateDashboard()
    })
    const traceEwelink = () => {
      if (disposed || new URLSearchParams(window.location.search).get('ewelinkDiagnostics') !== '1') return false
      ewelinkTraceTimer ??= setTimeout(() => {
        ewelinkTraceTimer = undefined
        console.info('eWeLink SignalR trace', {
          notices: ewelinkNotices, electricalNotices: ewelinkElectricalNotices, wrongProperty: ewelinkWrongProperty,
          refreshes: ewelinkRefreshes, refreshErrors: ewelinkRefreshErrors,
        })
        ewelinkNotices = ewelinkElectricalNotices = ewelinkWrongProperty = ewelinkRefreshes = ewelinkRefreshErrors = 0
      }, 10_000)
      return true
    }
    const scheduleEwelinkRefresh = () => {
      if (disposed || ewelinkFetching || !ewelinkDirty || ewelinkRefresh !== undefined) return
      ewelinkRefresh = setTimeout(() => {
        ewelinkRefresh = undefined
        if (disposed) return
        ewelinkDirty = false
        ewelinkFetching = true
        const queryKey = ['property', propertyId, 'ewelink']
        void Promise.all([client.invalidateQueries({ queryKey, exact: true }), invalidateDashboard()])
          .finally(() => {
            if (traceEwelink()) {
              const state = client.getQueryState(queryKey)
              if (state?.status === 'error') ewelinkRefreshErrors++
              else if (state?.status === 'success' && !state.isInvalidated) ewelinkRefreshes++
            }
            ewelinkFetching = false
            scheduleEwelinkRefresh()
          })
      }, 250)
    }
    connection.on('ewelinkChanged', (event: { propertyId: number; electricalTelemetry?: boolean }) => {
      if (traceEwelink()) {
        if (event.propertyId === propertyId) {
          ewelinkNotices++
          if (event.electricalTelemetry) ewelinkElectricalNotices++
        }
        else ewelinkWrongProperty++
      }
      if (event.propertyId !== propertyId) return
      ewelinkDirty = true
      scheduleEwelinkRefresh()
    })
    // Alerts: property views refresh on rule or incident changes; the bell and banner on any notification for this user.
    connection.on('alertsChanged', (event: { propertyId: number }) => {
      void client.invalidateQueries({ queryKey: ['property', event.propertyId, 'alerts'] })
      void client.invalidateQueries({ queryKey: ['notifications', 'summary'] })
    })
    connection.on('notificationsChanged', () => { void refreshNotifications(client) })
    connection.on('browserNotification', (message: unknown) => {
      window.dispatchEvent(new CustomEvent('homeautomation:browser-notification', { detail: message }))
    })
    connection.onreconnecting(disconnected)
    connection.onreconnected(subscribe)
    connection.onclose(() => { disconnected(); retryStart() })
    void start()

    return () => {
      disposed = true
      clearTimeout(retry)
      clearTimeout(ewelinkRefresh)
      clearTimeout(ewelinkTraceTimer)
      connection.off('stateChanged')
      connection.off('ewelinkChanged')
      connection.off('dashboardChanged')
      connection.off('alertsChanged')
      connection.off('notificationsChanged')
      connection.off('browserNotification')
      void connection.stop().catch(() => undefined)
    }
  }, [client, propertyId])

  return connectedProperty === propertyId
}
