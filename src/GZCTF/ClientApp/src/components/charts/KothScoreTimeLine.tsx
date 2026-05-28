import { useMantineColorScheme, useMantineTheme } from '@mantine/core'
import dayjs from 'dayjs'
import type { EChartsOption, SeriesOption } from 'echarts'
import { FC, useMemo } from 'react'
import { useTranslation } from 'react-i18next'
import { useParams } from 'react-router'
import { EchartsContainer } from '@Components/charts/EchartsContainer'
import { normalizeLanguage, useLanguage } from '@Utils/I18n'
import { useKothTimeline } from '@Hooks/useGame'

interface KothScoreTimeLineProps {
  /** Division-name filter; null = all divisions. */
  divisionName: string | null
}

/**
 * Per-round score chart for the King of the Hill scoreboard. Visual sibling
 * of <see cref="ScoreTimeLine" /> + <see cref="AdScoreTimeLine" /> — identical
 * echarts layout (axes / tooltip / zoom / legend) so the three boards look
 * the same when switching tabs. Data source is the new KotH-only timeline
 * endpoint, so the y-axis is hold credit only (no A&amp;D attack / SLA /
 * defense mixed in).
 */
export const KothScoreTimeLine: FC<KothScoreTimeLineProps> = ({ divisionName }) => {
  const { id } = useParams()
  const numId = parseInt(id ?? '-1')
  const theme = useMantineTheme()
  const { colorScheme } = useMantineColorScheme()
  const { t } = useTranslation()
  const { language } = useLanguage()
  const locale = normalizeLanguage(language)

  const { kothTimeline } = useKothTimeline(numId)

  const filteredTeams = useMemo(() => {
    if (!kothTimeline) return []
    if (divisionName === null) return kothTimeline.teams
    return kothTimeline.teams.filter((t: { division?: string | null }) => t.division === divisionName)
  }, [kothTimeline, divisionName])

  const chartData: SeriesOption[] = useMemo(() => {
    if (!kothTimeline || kothTimeline.teams.length === 0) return []

    const startSeed = kothTimeline.startedAt ? dayjs(kothTimeline.startedAt) : dayjs()

    return filteredTeams.map(
      (team: { teamName: string; items: { time: string; score: number }[] }) =>
        ({
          type: 'line',
          step: 'end',
          name: team.teamName,
          data: [
            [startSeed.toDate(), 0],
            ...team.items.map((p) => [p.time, p.score]),
          ],
        }) satisfies SeriesOption
    )
  }, [kothTimeline, filteredTeams])

  const staticOption: EChartsOption = useMemo(() => {
    const isDark = colorScheme === 'dark'
    const labelColor = isDark ? theme.colors.light[1] : theme.colors.dark[5]
    const lineColor = isDark ? theme.colors.gray[3] : theme.colors.gray[6]
    const backgroundColor = isDark ? theme.colors.gray[6] : theme.colors.light[1]

    return {
      animation: true,
      backgroundColor: 'transparent',
      toolbox: {
        show: true,
        feature: {
          dataZoom: {},
          restore: {},
          saveAsImage: {},
        },
      },
      xAxis: {
        type: 'time',
        min: kothTimeline?.startedAt ? dayjs(kothTimeline.startedAt).toDate() : undefined,
        max: kothTimeline?.endsAt ? dayjs(kothTimeline.endsAt).toDate() : undefined,
        splitLine: { show: false },
      },
      yAxis: {
        type: 'value',
        name: t('game.label.koth_hold_points', 'Hold points'),
        nameTextStyle: { color: labelColor, fontWeight: 'normal' },
        boundaryGap: [0, '100%'],
        axisLabel: {
          formatter: t('game.label.score_formatter', '{value}'),
          color: labelColor,
        },
        splitLine: {
          show: true,
          lineStyle: { color: [lineColor], type: 'dashed' },
        },
      },
      tooltip: {
        trigger: 'axis',
        borderWidth: 0,
        textStyle: { fontSize: 12, color: labelColor },
        backgroundColor: backgroundColor,
        formatter: (params: any) => {
          if (!Array.isArray(params)) return ''
          const escapeHtml = (str: string) => {
            const div = document.createElement('div')
            div.textContent = str
            return div.innerHTML
          }
          let res = `<div><p>${escapeHtml(dayjs(params[0].axisValue).format('YYYY-MM-DD HH:mm'))}</p>`
          params.sort((a, b) => (b.value?.[1] ?? 0) - (a.value?.[1] ?? 0))
          for (const item of params) {
            const rawName = item.seriesName ?? ''
            const name = rawName.length > 20 ? rawName.slice(0, 17) + '...' : rawName
            const escapedName = escapeHtml(name)
            const escapedValue = escapeHtml(Number(item.value?.[1] ?? 0).toFixed(1))
            res += `<div style="display:flex;justify-content:space-between;gap:1rem">
              <span>${item.marker} ${escapedName}</span>
              <span style="font-weight:bold">${escapedValue}</span>
            </div>`
          }
          res += '</div>'
          return res
        },
        extraCssText: 'max-width: 300px; white-space: normal; word-break: break-all',
      },
      legend: {
        type: 'scroll',
        orient: 'horizontal',
        top: 420,
        textStyle: { fontSize: 12, color: labelColor },
        formatter: (name: string) => (name.length > 20 ? name.slice(0, 17) + '...' : name),
      },
      grid: {
        top: 50,
        left: 70,
        right: 40,
        bottom: 110,
      },
      dataZoom: [
        { type: 'inside', start: 0, end: 100, xAxisIndex: 0, filterMode: 'none' },
        {
          type: 'slider',
          start: 0,
          end: 100,
          xAxisIndex: 0,
          showDetail: false,
          bottom: 60,
          height: 20,
        },
        { type: 'inside', start: 0, end: 100, yAxisIndex: 0, filterMode: 'none' },
        {
          type: 'slider',
          start: 0,
          end: 100,
          yAxisIndex: 0,
          showDetail: false,
          right: 10,
          width: 20,
        },
      ],
    } satisfies EChartsOption
  }, [t, kothTimeline?.startedAt, kothTimeline?.endsAt, colorScheme, theme])

  // No timeline data yet — render nothing rather than an empty axis box.
  if (!kothTimeline || kothTimeline.teams.length === 0 || kothTimeline.latestRound === 0) {
    return null
  }

  return (
    <EchartsContainer
      option={{ ...staticOption, series: chartData }}
      opts={{ renderer: 'svg', locale }}
      style={{ width: '100%', height: '460px', display: 'flex' }}
    />
  )
}
