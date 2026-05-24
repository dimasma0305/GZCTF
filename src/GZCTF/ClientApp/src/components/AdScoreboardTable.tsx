import {
  alpha,
  Avatar,
  Badge,
  Box,
  Center,
  Group,
  Paper,
  Stack,
  Table,
  Text,
  useMantineTheme,
} from '@mantine/core'
import { mdiSwordCross, mdiTrophyOutline } from '@mdi/js'
import { Icon } from '@mdi/react'
import cx from 'clsx'
import React, { FC } from 'react'
import { useTranslation } from 'react-i18next'
import { ScrollingText } from '@Components/ScrollingText'
import { useAdScoreboard } from '@Hooks/useGame'
import misc from '@Styles/Misc.module.css'
import classes from '@Styles/ScoreboardTable.module.css'

// Mirrors the jeopardy ScoreboardTable.tsx Widths/Lefts pattern so the
// pinned-left columns line up visually with the jeopardy board.
// Columns: [Rank, Team(name+avatar+division), Total, Attack, Defense Loss, SLA, Captures, Times Captured]
const Widths = [70, 240, 90, 90, 110, 80, 80, 110]
const Lefts = Widths.reduce(
  (acc, cur) => {
    acc.push(acc[acc.length - 1] + cur)
    return acc
  },
  [0]
)

interface AdScoreboardTableProps {
  numId: number
  /** Highlight this participation's row (the viewer's own team). */
  myParticipationId?: number | null
}

export const AdScoreboardTable: FC<AdScoreboardTableProps> = ({ numId, myParticipationId }) => {
  const { t } = useTranslation()
  const theme = useMantineTheme()
  const { adScoreboard } = useAdScoreboard(numId)

  if (!adScoreboard || adScoreboard.teams.length === 0 || adScoreboard.latestRound === 0) {
    return (
      <Paper shadow="md" p="xl">
        <Stack align="center" gap="xs">
          <Icon path={mdiSwordCross} size={2.5} color="var(--mantine-color-dimmed)" />
          <Text fw="bold" c="dimmed">
            {t('game.content.scoreboard.ad.empty.title', 'No A&D rounds yet')}
          </Text>
          <Text size="sm" c="dimmed">
            {t(
              'game.content.scoreboard.ad.empty.description',
              'The board will fill in once the first round completes.'
            )}
          </Text>
        </Stack>
      </Paper>
    )
  }

  const headers = [
    t('game.content.scoreboard.ad.column.rank', '#'),
    t('game.content.scoreboard.ad.column.team', 'Team'),
    t('game.content.scoreboard.ad.column.total', 'Total'),
    t('game.content.scoreboard.ad.column.attack', 'Attack'),
    t('game.content.scoreboard.ad.column.defense_loss', 'Defense loss'),
    t('game.content.scoreboard.ad.column.sla', 'SLA'),
    t('game.content.scoreboard.ad.column.captures', 'Captures'),
    t('game.content.scoreboard.ad.column.times_captured', 'Times captured'),
  ]

  return (
    <Paper shadow="md" p="md">
      <Stack gap="xs">
        <Group justify="space-between">
          <Group gap="xs">
            <Icon path={mdiTrophyOutline} size={1} />
            <Text fw="bold" size="sm">
              {t('game.content.scoreboard.ad.title', 'Attack & Defense scoreboard')}
            </Text>
          </Group>
          <Text size="xs" c="dimmed">
            {t('game.content.scoreboard.ad.latest_round', {
              round: adScoreboard.latestRound,
              defaultValue: 'through round {{round}}',
            })}
          </Text>
        </Group>

        <Box pos="relative" mih="calc(100vh - 18rem)">
          <Table.ScrollContainer
            minWidth="100%"
            classNames={{ scrollContainer: misc.noScrollBars }}
          >
            <Table className={classes.table}>
              <Table.Thead className={classes.thead}>
                <Table.Tr>
                  {headers.map((header, idx) => (
                    <Table.Th
                      key={idx}
                      className={cx(classes.left, classes.header)}
                      style={{
                        left: Lefts[idx],
                        width: Widths[idx],
                        minWidth: Widths[idx],
                        maxWidth: Widths[idx],
                      }}
                    >
                      {header}
                    </Table.Th>
                  ))}
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {adScoreboard.teams.map((row) => {
                  const isMine = myParticipationId === row.participationId
                  return (
                    <Table.Tr
                      key={row.participationId}
                      data-team-name={row.teamName}
                      style={
                        isMine
                          ? {
                              outline: `2px solid ${theme.colors[theme.primaryColor][4]}`,
                              outlineOffset: -2,
                              background: alpha(theme.colors[theme.primaryColor][4], 0.12),
                              transition: 'background .3s ease, outline .3s ease',
                            }
                          : undefined
                      }
                    >
                      {/* Rank — top 3 get colored badge, rest mono */}
                      <Table.Td
                        className={cx(classes.mono, classes.left)}
                        style={{ left: Lefts[0] }}
                      >
                        {row.rank <= 3 ? (
                          <Badge
                            color={['yellow', 'gray', 'orange'][row.rank - 1]}
                            variant="filled"
                          >
                            {row.rank}
                          </Badge>
                        ) : (
                          row.rank
                        )}
                      </Table.Td>

                      {/* Team — avatar + scrolling name + division line, identical layout to jeopardy */}
                      <Table.Td className={classes.left} style={{ left: Lefts[1] }}>
                        <Group justify="left" gap={5} wrap="nowrap" maw={Widths[1] - 10}>
                          <Avatar
                            imageProps={{ loading: 'lazy' }}
                            alt="avatar"
                            radius="xl"
                            size={30}
                            color={theme.primaryColor}
                          >
                            {row.teamName?.slice(0, 1) ?? 'T'}
                          </Avatar>
                          <Stack gap={0} h="2.5rem" justify="center" w={Widths[1] - 45}>
                            <ScrollingText size="sm" text={row.teamName || ''} />
                            {!!row.division && (
                              <Text size="xs" c="dimmed" ta="start" truncate className={classes.text}>
                                {row.division}
                              </Text>
                            )}
                          </Stack>
                        </Group>
                      </Table.Td>

                      {/* Total — bold mono (the headline number) */}
                      <Table.Td
                        className={cx(classes.mono, classes.left)}
                        style={{ left: Lefts[2] }}
                      >
                        {row.total.toFixed(1)}
                      </Table.Td>

                      {/* Attack points — teal */}
                      <Table.Td className={classes.mono}>
                        <Text size="sm" c="teal" className={misc.ffmono} fw={700}>
                          +{row.attackPoints.toFixed(1)}
                        </Text>
                      </Table.Td>

                      {/* Defense loss — red if non-zero, dimmed otherwise */}
                      <Table.Td className={classes.mono}>
                        <Text
                          size="sm"
                          c={row.defenseLoss > 0 ? 'red' : 'dimmed'}
                          className={misc.ffmono}
                          fw={700}
                        >
                          {row.defenseLoss > 0 ? `−${row.defenseLoss.toFixed(1)}` : '0.0'}
                        </Text>
                      </Table.Td>

                      {/* SLA — blue */}
                      <Table.Td className={classes.mono}>
                        <Text size="sm" c="blue" className={misc.ffmono} fw={700}>
                          {row.slaPoints.toFixed(1)}
                        </Text>
                      </Table.Td>

                      {/* Flags captured — plain mono */}
                      <Table.Td className={classes.mono}>{row.flagsCaptured}</Table.Td>

                      {/* Times captured — red when >0 */}
                      <Table.Td className={classes.mono}>
                        <Text
                          size="sm"
                          c={row.timesCaptured > 0 ? 'red' : 'dimmed'}
                          className={misc.ffmono}
                          fw={700}
                        >
                          {row.timesCaptured}
                        </Text>
                      </Table.Td>
                    </Table.Tr>
                  )
                })}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>

          {/* If no rows at all yet, show inline empty state */}
          {adScoreboard.teams.length === 0 && (
            <Center mih="6rem">
              <Text size="sm" c="dimmed">
                {t('game.content.scoreboard.ad.no_teams', 'No teams ranked yet.')}
              </Text>
            </Center>
          )}
        </Box>
      </Stack>
    </Paper>
  )
}
