import { useMantineColorScheme, useMantineTheme } from '@mantine/core'
import dayjs from 'dayjs'
import type { EChartsOption, SeriesOption } from 'echarts'
import { FC, useMemo } from 'react'
import { useTranslation } from 'react-i18next'
import { EchartsContainer } from '@Components/charts/EchartsContainer'
import { normalizeLanguage, useLanguage } from '@Utils/I18n'
import { AdScoreTimelineModel } from '@Api'

interface RoundScoreTimeLineProps {
  /** Per-round per-team timeline; undefined while loading. */
  timeline?: AdScoreTimelineModel
  /** Division-name filter; null = all divisions. */
  divisionName: string | null
  /** Already-translated y-axis label, e.g. "Score" or "Hold points". */
  yAxisName: string
}

/**
 * Shared per-round score chart behind both the A&D and KotH scoreboards.
 * Visual sibling of <see cref="ScoreTimeLine" /> — same echarts layout, axes,
 * tooltip, and zoom controls. The two boards differ only in their data source
 * and y-axis label, so those are the only props; the KotH and A&D timeline
 * endpoints return the same {@link AdScoreTimelineModel} shape.
 */
export const RoundScoreTimeLine: FC<RoundScoreTimeLineProps> = ({
  timeline,
  divisionName,
  yAxisName,
}) => {
  const theme = useMantineTheme()
  const { colorScheme } = useMantineColorScheme()
  const { t } = useTranslation()
  const { language } = useLanguage()
  const locale = normalizeLanguage(language)

  const filteredTeams = useMemo(() => {
    if (!timeline) return []
    if (divisionName === null) return timeline.teams
    return timeline.teams.filter((team) => team.division === divisionName)
  }, [timeline, divisionName])

  const chartData: SeriesOption[] = useMemo(() => {
    if (!timeline || timeline.teams.length === 0) return []

    const startSeed = timeline.startedAt ? dayjs(timeline.startedAt) : dayjs()

    return filteredTeams.map(
      (team) =>
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
  }, [timeline, filteredTeams])

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
        min: timeline?.startedAt ? dayjs(timeline.startedAt).toDate() : undefined,
        max: timeline?.endsAt ? dayjs(timeline.endsAt).toDate() : undefined,
        splitLine: { show: false },
      },
      yAxis: {
        type: 'value',
        name: yAxisName,
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
  }, [t, yAxisName, timeline?.startedAt, timeline?.endsAt, colorScheme, theme])

  // No timeline data yet — render nothing rather than an empty axis box.
  if (!timeline || timeline.teams.length === 0 || timeline.latestRound === 0) {
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
